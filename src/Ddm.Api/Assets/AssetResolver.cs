using Ddm.Api.Data;
using Ddm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Assets;

/// <summary>Maps the asset paths a page references to signed content URLs, in one query.</summary>
public sealed class AssetResolver(DdmDbContext db, ContentUrlSigner signer)
{
    public async Task<IReadOnlyDictionary<string, string>> UrlsForAsync(Project project, IReadOnlySet<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) return new Dictionary<string, string>();
        var wanted = paths.ToList();
        var rows = await db.Assets.AsNoTracking()
            .Where(a => a.ProjectId == project.Id && a.DeletedAt == null && wanted.Contains(a.Path))
            .Select(a => new { a.Path, a.Sha256 })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Path, r => signer.UrlFor(project.Id, r.Sha256), StringComparer.Ordinal);
    }
}
