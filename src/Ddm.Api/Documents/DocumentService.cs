using System.Security.Cryptography;
using System.Text;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Documents;

public sealed record WriteResult(string Path, ParsedMarkdown Parsed, ContentVersion Version, bool Created, bool Changed);

public sealed class DocumentService(DdmDbContext db, IBlobStore blobs)
{
    public const int MaxBytes = 1_048_576;
    public static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Validates, stores the body (content-addressed, so a failed DB commit only leaves a harmless orphan),
    /// then creates the version and moves the current pointer in one transaction.
    /// </summary>
    public async Task<WriteResult> WriteAsync(
        Caller caller, Project project, string path, byte[] bytes, string? message,
        WritePrecondition pre, bool requireIfMatch, CancellationToken ct)
    {
        if (bytes.Length > MaxBytes) throw ApiException.PayloadTooLarge($"Documents are limited to {MaxBytes} bytes");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw ApiException.BadRequest("invalid_encoding", "Document content must be valid UTF-8"); }
        var parsed = FrontMatter.Parse(text, path);

        var existing = await db.Documents.SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path, ct);
        var current = existing?.CurrentVersionId is { } cid ? await db.Versions.SingleAsync(v => v.Id == cid, ct) : null;
        CheckPreconditions(existing, current, pre, requireIfMatch);

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (current is not null && current.ContentSha256 == sha)
            return new(path, parsed, current, Created: false, Changed: false);

        var key = $"projects/{project.Id:N}/docs/{sha}.md";
        await blobs.PutAsync(key, bytes, "text/markdown; charset=utf-8", ct);

        var created = existing is null;
        var doc = existing ?? new Document { ProjectId = project.Id, Path = path };
        var version = new ContentVersion
        {
            ItemType = ItemType.Document, ItemId = doc.Id, Number = (current?.Number ?? 0) + 1,
            ContentRef = key, ContentSha256 = sha, Author = caller.Actor, Message = message,
        };
        doc.Title = parsed.Title;
        doc.FrontMatter = parsed.FrontMatterJson;
        doc.CurrentVersionId = version.Id;
        doc.UpdatedAt = version.CreatedAt;
        if (created) db.Documents.Add(doc);
        db.Versions.Add(version);
        db.Audit(caller, project.Id, created ? "doc.create" : "doc.update", path);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Another writer took this version number (or created this path) first.
            throw ApiException.PreconditionFailed("The document was changed by another writer; fetch the latest version and retry");
        }
        catch (DbUpdateConcurrencyException)
        {
            // The document row we read was deleted before we saved.
            throw ApiException.PreconditionFailed("The document was deleted by another writer");
        }
        return new(path, parsed, version, created, Changed: true);
    }

    private static void CheckPreconditions(Document? existing, ContentVersion? current, WritePrecondition pre, bool requireIfMatch)
    {
        if (existing is null)
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
        if (pre.IfMatchVersion is { } v && v != current!.Number)
            throw ApiException.PreconditionFailed($"The document is at version {current.Number}, not {v}");
    }

    public async Task<(Document Doc, ContentVersion Version)> GetCurrentAsync(Project project, string path, CancellationToken ct)
    {
        var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path, ct)
            ?? throw ApiException.NotFound("document_not_found", "Document not found");
        var version = await db.Versions.AsNoTracking().SingleAsync(v => v.Id == doc.CurrentVersionId, ct);
        return (doc, version);
    }

    public Task<bool> ExistsAsync(Project project, string path, CancellationToken ct) =>
        db.Documents.AnyAsync(d => d.ProjectId == project.Id && d.Path == path, ct);

    public async Task<byte[]> ReadContentAsync(ContentVersion version, CancellationToken ct) =>
        await blobs.GetAsync(version.ContentRef, ct)
        ?? throw new ApiException(500, "content_missing", "Stored content is missing", $"No object for {version.ContentRef}");

    public async Task<Document> RequireDocumentAsync(Project project, string path, CancellationToken ct) =>
        await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path, ct)
        ?? throw ApiException.NotFound("document_not_found", "Document not found");

    public async Task<ContentVersion> GetVersionAsync(Project project, string path, int number, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, ct);
        return await db.Versions.AsNoTracking().SingleOrDefaultAsync(
                   v => v.ItemType == ItemType.Document && v.ItemId == doc.Id && v.Number == number, ct)
               ?? throw ApiException.NotFound("version_not_found", "Version not found");
    }

    public async Task<Page<VersionDto>> ListVersionsAsync(Project project, string path, int? limit, string? cursor, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, ct);
        var take = Paging.ParseLimit(limit);
        var before = Paging.DecodeLongCursor(cursor);

        var query = db.Versions.AsNoTracking().Where(v => v.ItemType == ItemType.Document && v.ItemId == doc.Id);
        if (before is not null) query = query.Where(v => v.Number < before);
        var rows = await query.OrderByDescending(v => v.Number).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, v => v.Number.ToString());
        return new(page.Items.Select(v => new VersionDto(v.Number, v.Author, v.Message, v.CreatedAt)).ToList(), page.Next);
    }

    /// <summary>Restoring writes the old content as a new version; history is never rewritten.</summary>
    public async Task<WriteResult> RestoreAsync(
        Caller caller, Project project, string path, int number, WritePrecondition pre, CancellationToken ct)
    {
        var version = await GetVersionAsync(project, path, number, ct);
        var bytes = await ReadContentAsync(version, ct);
        return await WriteAsync(caller, project, path, bytes, $"Restore version {number}", pre, requireIfMatch: false, ct);
    }

    public async Task DeleteAsync(Caller caller, Project project, string path, WritePrecondition pre, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Lock the row so a concurrent write either commits before our If-Match check or fails after we delete.
        var doc = await db.Documents
                      .FromSqlInterpolated($"SELECT *, xmin FROM \"Documents\" WHERE \"ProjectId\" = {project.Id} AND \"Path\" = {path} FOR UPDATE")
                      .SingleOrDefaultAsync(ct)
                  ?? throw ApiException.NotFound("document_not_found", "Document not found");
        if (pre.IfMatchVersion is { } v)
        {
            var current = await db.Versions.AsNoTracking().SingleAsync(x => x.Id == doc.CurrentVersionId, ct);
            if (current.Number != v) throw ApiException.PreconditionFailed($"The document is at version {current.Number}, not {v}");
        }

        await db.Versions.Where(x => x.ItemType == ItemType.Document && x.ItemId == doc.Id).ExecuteDeleteAsync(ct);
        db.Documents.Remove(doc);
        db.Audit(caller, project.Id, "doc.delete", path);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
