namespace Ddm.Api.Assets;

/// <summary>
/// Splits the catch-all part of /assets/{**rest}. Asset paths must end in an allow-listed extension,
/// so a trailing "/tags" segment can never be part of one.
/// </summary>
public abstract record AssetRoute
{
    public sealed record Current(string Path) : AssetRoute;
    public sealed record Tags(string Path) : AssetRoute;

    private const string TagsSuffix = "/tags";

    public static AssetRoute Parse(string rest) =>
        rest.EndsWith(TagsSuffix, StringComparison.Ordinal) ? new Tags(rest[..^TagsSuffix.Length]) : new Current(rest);
}
