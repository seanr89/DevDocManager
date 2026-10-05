using System.Text.RegularExpressions;

namespace Ddm.Api.Documents;

/// <summary>
/// Splits the catch-all part of /docs/{**rest}. Document paths always end in ".md", which is
/// what makes "…/versions" and "…/tags" suffixes unambiguous.
/// </summary>
public abstract partial record DocRoute
{
    public sealed record Current(string Path) : DocRoute;
    public sealed record History(string Path) : DocRoute;
    public sealed record Snapshot(string Path, int Number) : DocRoute;
    public sealed record Restore(string Path, int Number) : DocRoute;
    public sealed record Tags(string Path) : DocRoute;

    [GeneratedRegex(@"^(?<path>.+\.md)(?:(?<tags>/tags)|/versions(?:/(?<n>[0-9]+)(?<restore>/restore)?)?)?\z")]
    private static partial Regex RouteRegex();

    public static DocRoute Parse(string rest)
    {
        var m = RouteRegex().Match(rest);
        if (!m.Success) return new Current(rest);
        var path = m.Groups["path"].Value;
        if (m.Groups["tags"].Success) return new Tags(path);
        if (!m.Groups["n"].Success) return m.Length == path.Length ? new Current(path) : new History(path);
        if (!int.TryParse(m.Groups["n"].Value, out var n)) n = 0;
        return m.Groups["restore"].Success ? new Restore(path, n) : new Snapshot(path, n);
    }
}
