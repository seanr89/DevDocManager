using Ddm.Api.Domain;

namespace Ddm.Api.Assets;

public sealed record AssetDto(string Path, string ContentType, long Size, string Sha256, IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAt, string UpdatedBy)
{
    public static AssetDto From(Asset a, IReadOnlyList<string> tags) =>
        new(a.Path, a.ContentType, a.Size, a.Sha256, tags, a.UpdatedAt, a.UpdatedBy);
}
