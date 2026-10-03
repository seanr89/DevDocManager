using System.Globalization;
using Ddm.Api.Common;

namespace Ddm.Api.Documents;

public readonly record struct WritePrecondition(int? IfMatchVersion, bool IfMatchAny, bool IfNoneMatchAny)
{
    public bool HasIfMatch => IfMatchVersion is not null || IfMatchAny;
}

public static class Preconditions
{
    public static string ETag(int version) => $"\"v{version}\"";

    public static WritePrecondition Parse(IHeaderDictionary headers)
    {
        int? version = null;
        var any = false;
        var ifMatch = headers.IfMatch;
        if (ifMatch.Count > 1) throw Invalid("Send a single If-Match value");
        if (ifMatch.Count == 1)
        {
            var raw = ifMatch[0]!.Trim();
            if (raw == "*") any = true;
            else if (TryParseVersion(raw, out var n)) version = n;
            else throw Invalid("If-Match must be a quoted version ETag such as \"v7\", or *");
        }

        var ifNoneMatch = headers.IfNoneMatch;
        var noneAny = ifNoneMatch.Count == 1 && ifNoneMatch[0]!.Trim() == "*";
        if (ifNoneMatch.Count > 0 && !noneAny) throw Invalid("If-None-Match only supports *");
        return new(version, any, noneAny);
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
