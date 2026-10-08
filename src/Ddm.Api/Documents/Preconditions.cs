using System.Globalization;
using System.Text.RegularExpressions;
using Ddm.Api.Common;

namespace Ddm.Api.Documents;

public readonly record struct WritePrecondition(int? IfMatchVersion, bool IfMatchAny, bool IfNoneMatchAny)
{
    public bool HasIfMatch => IfMatchVersion is not null || IfMatchAny;
}

/// <summary>Preconditions for content addressed by checksum (assets): the ETag is the quoted lowercase SHA-256.</summary>
public readonly record struct ShaPrecondition(string? IfMatchSha, bool IfMatchAny, bool IfNoneMatchAny)
{
    public bool HasIfMatch => IfMatchSha is not null || IfMatchAny;
}

public static partial class Preconditions
{
    public static string ETag(int version) => $"\"v{version}\"";
    public static string ShaETag(string sha256) => $"\"{sha256}\"";

    [GeneratedRegex("^\"[0-9a-f]{64}\"\\z")]
    private static partial Regex ShaETagRegex();

    public static WritePrecondition Parse(IHeaderDictionary headers)
    {
        var (ifMatch, noneAny) = Read(headers);
        if (ifMatch is null) return new(null, false, noneAny);
        if (ifMatch == "*") return new(null, true, noneAny);
        if (TryParseVersion(ifMatch, out var n)) return new(n, false, noneAny);
        throw Invalid("If-Match must be a quoted version ETag such as \"v7\", or *");
    }

    public static ShaPrecondition ParseSha(IHeaderDictionary headers)
    {
        var (ifMatch, noneAny) = Read(headers);
        if (ifMatch is null) return new(null, false, noneAny);
        if (ifMatch == "*") return new(null, true, noneAny);
        if (ShaETagRegex().IsMatch(ifMatch)) return new(ifMatch[1..^1], false, noneAny);
        throw Invalid("If-Match must be the asset's quoted SHA-256 ETag, or *");
    }

    private static (string? IfMatch, bool IfNoneMatchAny) Read(IHeaderDictionary headers)
    {
        var ifMatch = headers.IfMatch;
        if (ifMatch.Count > 1) throw Invalid("Send a single If-Match value");
        var ifNoneMatch = headers.IfNoneMatch;
        var noneAny = ifNoneMatch.Count == 1 && ifNoneMatch[0]!.Trim() == "*";
        if (ifNoneMatch.Count > 0 && !noneAny) throw Invalid("If-None-Match only supports *");
        return (ifMatch.Count == 1 ? ifMatch[0]!.Trim() : null, noneAny);
    }

    private static bool TryParseVersion(string raw, out int n)
    {
        n = 0;
        return raw.Length >= 4 && raw.StartsWith("\"v", StringComparison.Ordinal) && raw.EndsWith('"')
               && int.TryParse(raw.AsSpan(2, raw.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out n)
               && n > 0;
    }

    private static ApiException Invalid(string detail) => ApiException.BadRequest("invalid_etag", "Invalid ETag precondition", detail);
}
