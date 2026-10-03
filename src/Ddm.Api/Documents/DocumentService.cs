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
}
