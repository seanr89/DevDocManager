using System.Security.Cryptography;
using System.Text.Json;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Specs;

/// <summary>A validated spec, ready to store. Preparing touches neither the database nor blob storage.</summary>
public sealed record PreparedSpec(string Name, byte[] Bytes, string Sha256, ValidatedSpec Spec)
{
    public string BlobKey(Guid projectId) => $"projects/{projectId:N}/specs/{Sha256}.{Spec.Format}";
    public string NormalizedKey(Guid projectId) => $"projects/{projectId:N}/specs/{Sha256}.normalized.json";
}

public sealed record SpecState(Spec Spec, ContentVersion? Current)
{
    public bool IsLive => Spec.DeletedAt is null;
}

public sealed record SpecWriteResult(Spec Spec, ContentVersion Version, bool Created, bool Changed);

public sealed class SpecService(DdmDbContext db, IBlobStore blobs, IOptions<SpecOptions> options)
{
    public long MaxBytes => options.Value.MaxBytes;

    public static string MediaTypeFor(string format) =>
        format == "json" ? "application/json; charset=utf-8" : "application/yaml; charset=utf-8";

    public async Task<PreparedSpec> PrepareAsync(string name, byte[] bytes, CancellationToken ct)
    {
        SpecName.Require(name);
        if (bytes.Length > MaxBytes) throw ApiException.PayloadTooLarge($"Specs are limited to {MaxBytes} bytes");
        var spec = await SpecValidator.ValidateAsync(bytes, ct);
        return new(name, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), spec);
    }

    public async Task UploadAsync(Project project, PreparedSpec prepared, CancellationToken ct)
    {
        await blobs.PutAsync(prepared.BlobKey(project.Id), prepared.Bytes, MediaTypeFor(prepared.Spec.Format), ct);
        await blobs.PutAsync(prepared.NormalizedKey(project.Id), prepared.Spec.NormalizedJson, "application/json", ct);
    }

    public async Task<SpecWriteResult> WriteAsync(
        Caller caller, Project project, PreparedSpec prepared, string? message, WritePrecondition pre, bool requireIfMatch, CancellationToken ct)
    {
        var state = await FindAsync(project, prepared.Name, ct);
        var live = state is { IsLive: true } ? state : null;
        CheckPreconditions(live, pre, requireIfMatch);
        if (live?.Current is { } current && current.ContentSha256 == prepared.Sha256)
            return new(live.Spec, current, Created: false, Changed: false);

        await UploadAsync(project, prepared, ct);
        var result = Stage(caller, project, prepared, state, message);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.PreconditionFailed("The spec was changed by another writer; fetch the latest version and retry");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiException.PreconditionFailed("The spec was changed or deleted by another writer");
        }
        return result;
    }

    /// <summary>Stages the next version, creating, updating or reviving the row. Does not save or upload.</summary>
    public SpecWriteResult Stage(Caller caller, Project project, PreparedSpec prepared, SpecState? state, string? message)
    {
        var spec = state?.Spec ?? new Spec { ProjectId = project.Id, Name = prepared.Name };
        var created = state is not { IsLive: true };
        var version = new ContentVersion
        {
            ItemType = ItemType.Spec, ItemId = spec.Id, Number = (state?.Current?.Number ?? 0) + 1,
            ContentRef = prepared.BlobKey(project.Id), NormalizedRef = prepared.NormalizedKey(project.Id),
            ContentSha256 = prepared.Sha256, Author = caller.Actor, Message = message,
        };
        var v = prepared.Spec;
        spec.Title = Truncate(v.Title, 300);
        spec.ApiVersion = Truncate(v.ApiVersion, 100);
        spec.OpenApiVersion = v.OpenApiVersion;
        spec.Format = v.Format;
        spec.Operations = JsonSerializer.Serialize(v.Operations, JsonSerializerOptions.Web);
        spec.OperationCount = v.Operations.Count;
        spec.CurrentVersionId = version.Id;
        spec.UpdatedAt = version.CreatedAt;
        spec.DeletedAt = null;
        if (state is null) db.Specs.Add(spec);
        db.Versions.Add(version);
        db.Audit(caller, project.Id, created ? "spec.create" : "spec.update", prepared.Name);
        return new(spec, version, created, Changed: true);
    }

    /// <summary>Tombstones a tracked, live spec. Does not save.</summary>
    public void StageDelete(Caller caller, Project project, Spec spec)
    {
        spec.DeletedAt = DateTimeOffset.UtcNow;
        spec.UpdatedAt = spec.DeletedAt.Value;
        db.Audit(caller, project.Id, "spec.delete", spec.Name);
    }

    /// <summary>The row with this name, including a tombstone, tracked for update.</summary>
    public async Task<SpecState?> FindAsync(Project project, string name, CancellationToken ct)
    {
        var spec = await db.Specs.SingleOrDefaultAsync(s => s.ProjectId == project.Id && s.Name == name, ct);
        if (spec is null) return null;
        var current = spec.CurrentVersionId is { } cid ? await db.Versions.SingleAsync(v => v.Id == cid, ct) : null;
        return new(spec, current);
    }

    public async Task<Spec> RequireAsync(Project project, string name, bool includeDeleted, CancellationToken ct) =>
        await db.Specs.AsNoTracking()
            .SingleOrDefaultAsync(s => s.ProjectId == project.Id && s.Name == name && (includeDeleted || s.DeletedAt == null), ct)
        ?? throw ApiException.NotFound("spec_not_found", "Spec not found");

    public async Task<(Spec Spec, ContentVersion Version)> GetCurrentAsync(Project project, string name, CancellationToken ct)
    {
        var spec = await RequireAsync(project, name, includeDeleted: false, ct);
        return (spec, await db.Versions.AsNoTracking().SingleAsync(v => v.Id == spec.CurrentVersionId, ct));
    }

    public async Task<ContentVersion> GetVersionAsync(Project project, string name, int number, CancellationToken ct)
    {
        var spec = await RequireAsync(project, name, includeDeleted: true, ct);
        return await db.Versions.AsNoTracking().SingleOrDefaultAsync(
                   v => v.ItemType == ItemType.Spec && v.ItemId == spec.Id && v.Number == number, ct)
               ?? throw ApiException.NotFound("version_not_found", "Version not found");
    }

    public async Task<Page<VersionDto>> ListVersionsAsync(Project project, string name, int? limit, string? cursor, CancellationToken ct)
    {
        var spec = await RequireAsync(project, name, includeDeleted: true, ct);
        var take = Paging.ParseLimit(limit);
        var before = Paging.DecodeLongCursor(cursor);
        var query = db.Versions.AsNoTracking().Where(v => v.ItemType == ItemType.Spec && v.ItemId == spec.Id);
        if (before is not null) query = query.Where(v => v.Number < before);
        var rows = await query.OrderByDescending(v => v.Number).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, v => v.Number.ToString());
        return new(page.Items.Select(v => new VersionDto(v.Number, v.Author, v.Message, v.CreatedAt)).ToList(), page.Next);
    }

    public async Task<byte[]> ReadAsync(string key, CancellationToken ct) =>
        await blobs.GetAsync(key, ct) ?? throw new ApiException(500, "content_missing", "Stored content is missing", $"No object for {key}");

    public async Task<SpecWriteResult> RestoreAsync(
        Caller caller, Project project, string name, int number, WritePrecondition pre, CancellationToken ct)
    {
        var version = await GetVersionAsync(project, name, number, ct);
        var bytes = await ReadAsync(version.ContentRef, ct);
        return await WriteAsync(caller, project, await PrepareAsync(name, bytes, ct), $"Restore version {number}", pre, requireIfMatch: false, ct);
    }

    /// <summary>The row version makes a delete racing an update fail cleanly, whichever commits second.</summary>
    public async Task DeleteAsync(Caller caller, Project project, string name, WritePrecondition pre, CancellationToken ct)
    {
        var state = await FindAsync(project, name, ct);
        if (state is not { IsLive: true }) throw ApiException.NotFound("spec_not_found", "Spec not found");
        if (pre.IfMatchVersion is { } v && v != state.Current!.Number)
            throw ApiException.PreconditionFailed($"The spec is at version {state.Current.Number}, not {v}");
        StageDelete(caller, project, state.Spec);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiException.PreconditionFailed("The spec was changed by another writer");
        }
    }

    private static void CheckPreconditions(SpecState? live, WritePrecondition pre, bool requireIfMatch)
    {
        if (live is null)
        {
            if (pre.HasIfMatch) throw ApiException.PreconditionFailed("The spec does not exist");
            return;
        }
        if (pre.IfNoneMatchAny) throw ApiException.PreconditionFailed("The spec already exists");
        if (!pre.HasIfMatch)
        {
            if (requireIfMatch)
                throw ApiException.PreconditionRequired("Updating an existing spec requires an If-Match header with its current ETag");
            return;
        }
        if (pre.IfMatchVersion is { } v && v != live.Current!.Number)
            throw ApiException.PreconditionFailed($"The spec is at version {live.Current.Number}, not {v}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
