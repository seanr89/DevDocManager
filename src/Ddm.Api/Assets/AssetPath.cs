using Ddm.Api.Common;

namespace Ddm.Api.Assets;

public static class AssetPath
{
    /// <summary>Validates an asset path and returns its type: 400 invalid_path for a bad path or markdown, 415 off the allow-list.</summary>
    public static AssetType Require(string? path)
    {
        if (ContentPath.ValidateSegments(path) is { } error) throw ApiException.BadRequest("invalid_path", "Invalid asset path", error);
        if (path!.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("invalid_path", "Invalid asset path", "Markdown files are documents: publish them under /docs");
        return AssetTypes.ForPath(path)
               ?? throw new ApiException(415, "unsupported_asset_type", "This file type is not allowed",
                   $"Allowed extensions: {string.Join(", ", AssetTypes.Extensions)}");
    }

    public static bool IsValid(string path) =>
        ContentPath.ValidateSegments(path) is null
        && !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && AssetTypes.ForPath(path) is not null;
}
