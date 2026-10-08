using Ddm.Api.Common;

namespace Ddm.Api.Documents;

public static class DocumentPath
{
    public const int MaxLength = ContentPath.MaxLength;

    /// <summary>Returns an error message, or null when the path is valid.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Path is required";
        if (!path.EndsWith(".md", StringComparison.Ordinal)) return "Path must end with .md";
        return ContentPath.ValidateSegments(path);
    }

    public static string Require(string? path) =>
        Validate(path) is { } error
            ? throw ApiException.BadRequest("invalid_path", "Invalid document path", error)
            : path!;
}
