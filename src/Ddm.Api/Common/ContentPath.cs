using System.Text.RegularExpressions;

namespace Ddm.Api.Common;

/// <summary>Path rules shared by documents and assets: '/'-separated segments, no dot-files, bounded size.</summary>
public static partial class ContentPath
{
    public const int MaxLength = 255;
    public const int MaxSegments = 10;
    public const int MaxSegmentLength = 100;

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]*\z")]
    private static partial Regex SegmentRegex();

    /// <summary>Returns an error message, or null when the path and every segment are valid.</summary>
    public static string? ValidateSegments(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Path is required";
        if (path.Length > MaxLength) return $"Path is limited to {MaxLength} characters";
        var segments = path.Split('/');
        if (segments.Length > MaxSegments) return $"Path is limited to {MaxSegments} segments";
        foreach (var s in segments)
            if (s.Length > MaxSegmentLength || !SegmentRegex().IsMatch(s))
                return $"Invalid path segment '{s}': use letters, digits, '.', '_' and '-', starting with a letter, digit or '_'";
        return null;
    }

    /// <summary>A folder prefix is empty (the project root) or valid segments followed by '/'. Returns an error or null.</summary>
    public static string? ValidateFolder(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return null;
        if (!prefix.EndsWith('/')) return "A folder prefix must end with '/'";
        return ValidateSegments(prefix[..^1]);
    }
}
