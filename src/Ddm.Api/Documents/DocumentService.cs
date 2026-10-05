using System.Security.Cryptography;
using System.Text;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Documents;

public sealed record WriteResult(Guid DocumentId, string Path, ParsedMarkdown Parsed, ContentVersion Version, bool Created, bool Changed);

/// <summary>A validated document body, ready to store. Preparing touches neither the database nor blob storage.</summary>
public sealed record PreparedDocument(string Path, byte[] Bytes, string Sha256, ParsedMarkdown Parsed)
{
    public string BlobKey(Guid projectId) => $"projects/{projectId:N}/docs/{Sha256}.md";
}

/// <summary>A document row, live or tombstoned, and the version its pointer names.</summary>
public sealed record DocumentState(Document Doc, ContentVersion? Current)
{
    public bool IsLive => Doc.DeletedAt is null;
}

public sealed class DocumentService(DdmDbContext db, IBlobStore blobs)
{
    public const int MaxBytes = 1_048_576;
    public static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PreparedDocument Prepare(string path, byte[] bytes)
    {
        DocumentPath.Require(path);
        if (bytes.Length > MaxBytes) throw ApiException.PayloadTooLarge($"Documents are limited to {MaxBytes} bytes");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw ApiException.BadRequest("invalid_encoding", "Document content must be valid UTF-8"); }
        var parsed = FrontMatter.Parse(text, path);
        return new(path, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), parsed);
    }

    public Task UploadAsync(Project project, PreparedDocument prepared, CancellationToken ct) =>
        blobs.PutAsync(prepared.BlobKey(project.Id), prepared.Bytes, "text/markdown; charset=utf-8", ct);

    /// <summary>
    /// Single-item write: checks preconditions, stores the body (content-addressed, so a failed DB commit only
    /// leaves a harmless orphan), then stages and commits the version. A tombstone counts as missing.
    /// </summary>
    public async Task<WriteResult> WriteAsync(
        Caller caller, Project project, PreparedDocument prepared, string? message,
        WritePrecondition pre, bool requireIfMatch, CancellationToken ct)
    {
        var state = await FindAsync(project, prepared.Path, ct);
        var live = state is { IsLive: true } ? state : null;
        CheckPreconditions(live, pre, requireIfMatch);
        if (live?.Current is { } current && current.ContentSha256 == prepared.Sha256)
            return new(live.Doc.Id, prepared.Path, prepared.Parsed, current, Created: false, Changed: false);

        await UploadAsync(project, prepared, ct);
        var result = await StageAsync(caller, project, prepared, state, message, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Another writer took this version number (or created this path) first.
            throw ApiException.PreconditionFailed("The document was changed by another writer; fetch the latest version and retry");
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row we read was updated or deleted before we saved (its xmin moved on).
            throw ApiException.PreconditionFailed("The document was changed or deleted by another writer");
        }
        return result;
    }

    /// <summary>
    /// Stages the next version, creating, updating or reviving the row. Does not save or upload: the caller
    /// has checked preconditions and stored the blob. Reviving continues the version numbering.
    /// </summary>
    public Task<WriteResult> StageAsync(
        Caller caller, Project project, PreparedDocument prepared, DocumentState? state, string? message, CancellationToken ct)
    {
        var doc = state?.Doc ?? new Document { ProjectId = project.Id, Path = prepared.Path };
        var created = state is not { IsLive: true };
        var version = new ContentVersion
        {
            ItemType = ItemType.Document, ItemId = doc.Id, Number = (state?.Current?.Number ?? 0) + 1,
            ContentRef = prepared.BlobKey(project.Id), ContentSha256 = prepared.Sha256, Author = caller.Actor, Message = message,
        };
        doc.Title = prepared.Parsed.Title;
        doc.FrontMatter = prepared.Parsed.FrontMatterJson;
        doc.CurrentVersionId = version.Id;
        doc.UpdatedAt = version.CreatedAt;
        doc.DeletedAt = null;
        if (state is null) db.Documents.Add(doc);
        db.Versions.Add(version);
        db.Audit(caller, project.Id, created ? "doc.create" : "doc.update", prepared.Path);
        return Task.FromResult(new WriteResult(doc.Id, prepared.Path, prepared.Parsed, version, created, Changed: true));
    }

    /// <summary>Tombstones a tracked, live document. Does not save.</summary>
    public void StageDelete(Caller caller, Project project, Document doc)
    {
        doc.DeletedAt = DateTimeOffset.UtcNow;
        doc.UpdatedAt = doc.DeletedAt.Value;
        db.Audit(caller, project.Id, "doc.delete", doc.Path);
    }

    /// <summary>The row at this path, including a tombstone, tracked for update.</summary>
    public async Task<DocumentState?> FindAsync(Project project, string path, CancellationToken ct)
    {
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path, ct);
        if (doc is null) return null;
        var current = doc.CurrentVersionId is { } cid ? await db.Versions.SingleAsync(v => v.Id == cid, ct) : null;
        return new(doc, current);
    }

    private static void CheckPreconditions(DocumentState? live, WritePrecondition pre, bool requireIfMatch)
    {
        if (live is null)
        {
            if (pre.HasIfMatch) throw ApiException.PreconditionFailed("The document does not exist");
            return;
        }
        if (pre.IfNoneMatchAny) throw ApiException.PreconditionFailed("The document already exists");
        if (!pre.HasIfMatch)
        {
            if (requireIfMatch)
                throw ApiException.PreconditionRequired("Updating an existing document requires an If-Match header with its current ETag");
            return;
        }
        if (pre.IfMatchVersion is { } v && v != live.Current!.Number)
            throw ApiException.PreconditionFailed($"The document is at version {live.Current.Number}, not {v}");
    }

    public async Task<(Document Doc, ContentVersion Version)> GetCurrentAsync(Project project, string path, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, includeDeleted: false, ct);
        var version = await db.Versions.AsNoTracking().SingleAsync(v => v.Id == doc.CurrentVersionId, ct);
        return (doc, version);
    }

    public Task<bool> ExistsAsync(Project project, string path, CancellationToken ct) =>
        db.Documents.AnyAsync(d => d.ProjectId == project.Id && d.Path == path && d.DeletedAt == null, ct);

    public async Task<byte[]> ReadContentAsync(ContentVersion version, CancellationToken ct) =>
        await blobs.GetAsync(version.ContentRef, ct)
        ?? throw new ApiException(500, "content_missing", "Stored content is missing", $"No object for {version.ContentRef}");

    public async Task<Document> RequireDocumentAsync(Project project, string path, bool includeDeleted, CancellationToken ct) =>
        await db.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path && (includeDeleted || d.DeletedAt == null), ct)
        ?? throw ApiException.NotFound("document_not_found", "Document not found");

    /// <summary>Versions stay readable after a delete, so a tombstoned document can be inspected and restored.</summary>
    public async Task<ContentVersion> GetVersionAsync(Project project, string path, int number, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, includeDeleted: true, ct);
        return await db.Versions.AsNoTracking().SingleOrDefaultAsync(
                   v => v.ItemType == ItemType.Document && v.ItemId == doc.Id && v.Number == number, ct)
               ?? throw ApiException.NotFound("version_not_found", "Version not found");
    }

    public async Task<Page<VersionDto>> ListVersionsAsync(Project project, string path, int? limit, string? cursor, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, includeDeleted: true, ct);
        var take = Paging.ParseLimit(limit);
        var before = Paging.DecodeLongCursor(cursor);

        var query = db.Versions.AsNoTracking().Where(v => v.ItemType == ItemType.Document && v.ItemId == doc.Id);
        if (before is not null) query = query.Where(v => v.Number < before);
        var rows = await query.OrderByDescending(v => v.Number).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, v => v.Number.ToString());
        return new(page.Items.Select(v => new VersionDto(v.Number, v.Author, v.Message, v.CreatedAt)).ToList(), page.Next);
    }

    /// <summary>Restoring writes the old content as a new version; history is never rewritten. Revives a tombstone.</summary>
    public async Task<WriteResult> RestoreAsync(
        Caller caller, Project project, string path, int number, WritePrecondition pre, CancellationToken ct)
    {
        var version = await GetVersionAsync(project, path, number, ct);
        var bytes = await ReadContentAsync(version, ct);
        return await WriteAsync(caller, project, Prepare(path, bytes), $"Restore version {number}", pre, requireIfMatch: false, ct);
    }

    public async Task DeleteAsync(Caller caller, Project project, string path, WritePrecondition pre, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Lock the row so a concurrent write either commits before our If-Match check or fails on its stale xmin.
        // xmin is a system column, so SELECT * does not include it: name it explicitly for the RowVersion mapping.
        var doc = await db.Documents
                      .FromSqlInterpolated($"SELECT *, xmin FROM \"Documents\" WHERE \"ProjectId\" = {project.Id} AND \"Path\" = {path} AND \"DeletedAt\" IS NULL FOR UPDATE")
                      .SingleOrDefaultAsync(ct)
                  ?? throw ApiException.NotFound("document_not_found", "Document not found");
        if (pre.IfMatchVersion is { } v)
        {
            var current = await db.Versions.AsNoTracking().SingleAsync(x => x.Id == doc.CurrentVersionId, ct);
            if (current.Number != v) throw ApiException.PreconditionFailed($"The document is at version {current.Number}, not {v}");
        }

        StageDelete(caller, project, doc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
