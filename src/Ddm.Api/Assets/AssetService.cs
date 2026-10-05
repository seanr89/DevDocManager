using System.Security.Cryptography;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Assets;

/// <summary>A validated upload, ready to store. Preparing touches neither the database nor blob storage.</summary>
public sealed record PreparedAsset(string Path, byte[] Bytes, string Sha256, AssetType Type)
{
    public string BlobKey(Guid projectId) => $"projects/{projectId:N}/assets/{Sha256}";
}

public sealed record AssetWriteResult(Asset Asset, bool Created, bool Changed);

public sealed class AssetService(DdmDbContext db, IBlobStore blobs, IOptions<AssetOptions> options)
{
    public long MaxBytes => options.Value.MaxBytes;

    /// <summary>Validates path, size and type; the type is confirmed from the bytes, never from the client's Content-Type.</summary>
    public PreparedAsset Prepare(string path, byte[] bytes)
    {
        var type = AssetPath.Require(path);
        if (bytes.Length > MaxBytes) throw ApiException.PayloadTooLarge($"Assets are limited to {MaxBytes} bytes");
        if (!type.Sniff(bytes))
            throw ApiException.BadRequest("asset_type_mismatch", "The file's content does not match its extension",
                $"{path} does not contain {type.MediaType} data");
        return new(path, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), type);
    }

    public Task UploadAsync(Project project, PreparedAsset prepared, CancellationToken ct) =>
        blobs.PutAsync(prepared.BlobKey(project.Id), prepared.Bytes, prepared.Type.MediaType, ct);

    public async Task<AssetWriteResult> WriteAsync(
        Caller caller, Project project, PreparedAsset prepared, ShaPrecondition pre, bool requireIfMatch, CancellationToken ct)
    {
        var existing = await FindAsync(project, prepared.Path, ct);
        var live = existing is { DeletedAt: null } ? existing : null;
        CheckPreconditions(live, pre, requireIfMatch);
        if (live is not null && live.Sha256 == prepared.Sha256) return new(live, Created: false, Changed: false);

        await UploadAsync(project, prepared, ct);
        var result = Stage(caller, project, prepared, existing);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.PreconditionFailed("The asset was created by another writer; fetch it and retry");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiException.PreconditionFailed("The asset was changed or deleted by another writer");
        }
        return result;
    }

    /// <summary>Stages creating, replacing or reviving the row. The caller has stored the blob and will save.</summary>
    public AssetWriteResult Stage(Caller caller, Project project, PreparedAsset prepared, Asset? existing)
    {
        var created = existing is not { DeletedAt: null };
        var asset = existing ?? new Asset
        {
            ProjectId = project.Id, Path = prepared.Path, ContentType = "", Sha256 = "", StorageKey = "", UpdatedBy = "",
        };
        asset.ContentType = prepared.Type.MediaType;
        asset.Size = prepared.Bytes.Length;
        asset.Sha256 = prepared.Sha256;
        asset.StorageKey = prepared.BlobKey(project.Id);
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        asset.UpdatedBy = caller.Actor;
        asset.DeletedAt = null;
        if (existing is null) db.Assets.Add(asset);
        db.Audit(caller, project.Id, created ? "asset.create" : "asset.update", prepared.Path);
        return new(asset, created, Changed: true);
    }

    /// <summary>Tombstones a tracked, live asset. The blob stays, since old document versions may still show it.</summary>
    public void StageDelete(Caller caller, Project project, Asset asset)
    {
        asset.DeletedAt = DateTimeOffset.UtcNow;
        asset.UpdatedAt = asset.DeletedAt.Value;
        asset.UpdatedBy = caller.Actor;
        db.Audit(caller, project.Id, "asset.delete", asset.Path);
    }

    /// <summary>The row at this path, including a tombstone, tracked for update.</summary>
    public Task<Asset?> FindAsync(Project project, string path, CancellationToken ct) =>
        db.Assets.SingleOrDefaultAsync(a => a.ProjectId == project.Id && a.Path == path, ct);

    public Task<bool> ExistsAsync(Project project, string path, CancellationToken ct) =>
        db.Assets.AnyAsync(a => a.ProjectId == project.Id && a.Path == path && a.DeletedAt == null, ct);

    public async Task<Asset> RequireLiveAsync(Project project, string path, CancellationToken ct) =>
        await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.ProjectId == project.Id && a.Path == path && a.DeletedAt == null, ct)
        ?? throw ApiException.NotFound("asset_not_found", "Asset not found");

    /// <summary>The row version makes a delete racing a replace fail cleanly, whichever commits second.</summary>
    public async Task DeleteAsync(Caller caller, Project project, string path, ShaPrecondition pre, CancellationToken ct)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.ProjectId == project.Id && a.Path == path && a.DeletedAt == null, ct)
                    ?? throw ApiException.NotFound("asset_not_found", "Asset not found");
        if (pre.IfMatchSha is { } sha && sha != asset.Sha256)
            throw ApiException.PreconditionFailed("The asset has changed since you fetched it");
        StageDelete(caller, project, asset);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiException.PreconditionFailed("The asset was changed by another writer");
        }
    }

    private static void CheckPreconditions(Asset? live, ShaPrecondition pre, bool requireIfMatch)
    {
        if (live is null)
        {
            if (pre.HasIfMatch) throw ApiException.PreconditionFailed("The asset does not exist");
            return;
        }
        if (pre.IfNoneMatchAny) throw ApiException.PreconditionFailed("The asset already exists");
        if (!pre.HasIfMatch)
        {
            if (requireIfMatch)
                throw ApiException.PreconditionRequired("Replacing an existing asset requires an If-Match header with its current ETag");
            return;
        }
        if (pre.IfMatchSha is { } sha && sha != live.Sha256)
            throw ApiException.PreconditionFailed("The asset has changed since you fetched it");
    }
}
