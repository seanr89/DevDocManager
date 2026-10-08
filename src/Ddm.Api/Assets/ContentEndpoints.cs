using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Assets;

public static class ContentEndpoints
{
    /// <summary>Anonymous: the signature is the capability. Outside /api/v1 so it can live on a separate content origin.</summary>
    public static void MapContent(this WebApplication app) =>
        app.MapGet("/content/{projectId:guid}/{sha}", ServeAsync).AllowAnonymous();

    private static async Task<IResult> ServeAsync(
        Guid projectId, string sha, long? exp, string? sig, HttpContext http,
        ContentUrlSigner signer, TimeProvider time, DdmDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var expiry = exp ?? 0;
        switch (signer.Verify(projectId, sha, expiry, sig))
        {
            case SignatureCheck.Invalid: throw ApiException.Forbidden("invalid_signature", "The content link is not valid");
            case SignatureCheck.Expired: throw ApiException.Forbidden("signature_expired", "The content link has expired");
        }

        // Tombstoned assets still count: a page rendered before the delete may still show the image.
        // The same bytes can sit at an .svg and a .txt path, so prefer live rows, then image rows, so an image link serves as an image.
        var asset = await db.Assets.AsNoTracking()
                        .Where(a => a.ProjectId == projectId && a.Sha256 == sha)
                        .OrderBy(a => a.DeletedAt != null)
                        .ThenBy(a => !a.ContentType.StartsWith("image/"))
                        .ThenBy(a => a.Path)
                        .FirstOrDefaultAsync(ct)
                    ?? throw ApiException.NotFound("not_found", "Content not found");
        var maxAge = Math.Max(0, expiry - time.GetUtcNow().ToUnixTimeSeconds());
        return await AssetResponses.ServeAsync(http, blobs, asset.StorageKey, asset.ContentType, asset.Size, asset.Sha256,
            AssetResponses.FileNameOf(asset.Path), $"private, max-age={maxAge}", ct);
    }
}
