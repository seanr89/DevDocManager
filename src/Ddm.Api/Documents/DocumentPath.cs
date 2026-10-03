using System.Text.RegularExpressions;
using Ddm.Api.Common;

namespace Ddm.Api.Documents;

public static partial class DocumentPath
{
    public const int MaxLength = 255;
    public const int MaxSegments = 10;
    public const int MaxSegmentLength = 100;

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]*\z")]
    private static partial Regex SegmentRegex();

    /// <summary>Returns an error message, or null when the path is valid.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Path is required";
        if (path.Length > MaxLength) return $"Path is limited to {MaxLength} characters";
        if (!path.EndsWith(".md", StringComparison.Ordinal)) return "Path must end with .md";
        var segments = path.Split('/');
        if (segments.Length > MaxSegments) return $"Path is limited to {MaxSegments} segments";
        foreach (var s in segments)
            if (s.Length > MaxSegmentLength || !SegmentRegex().IsMatch(s))
                return $"Invalid path segment '{s}': use letters, digits, '.', '_' and '-', starting with a letter, digit or '_'";
        return null;
    }

    public static string Require(string? path) =>
        Validate(path) is { } error
            ? throw ApiException.BadRequest("invalid_path", "Invalid document path", error)
            : path!;
}
