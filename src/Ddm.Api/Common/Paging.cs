using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Ddm.Api.Common;

public sealed record Page<T>(IReadOnlyList<T> Items, string? Next);

public static class Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    private static readonly UTF8Encoding Strict = new(false, true);

    public static int ParseLimit(int? limit)
    {
        if (limit is null) return DefaultLimit;
        if (limit < 1) throw ApiException.BadRequest("invalid_limit", "limit must be at least 1");
        return Math.Min(limit.Value, MaxLimit);
    }

    public static string EncodeCursor(string key) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(key));

    public static string? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try { return Strict.GetString(Base64Url.DecodeFromChars(cursor)); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
        {
            throw ApiException.BadRequest("invalid_cursor", "The cursor is not valid");
        }
    }

    public static long? DecodeLongCursor(string? cursor)
    {
        var raw = DecodeCursor(cursor);
        if (raw is null) return null;
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw ApiException.BadRequest("invalid_cursor", "The cursor is not valid");
    }

    /// <summary>Rows must have been fetched with <c>limit + 1</c>.</summary>
    public static Page<T> ToPage<T>(List<T> rows, int limit, Func<T, string> keyOf)
    {
        if (rows.Count <= limit) return new(rows, null);
        var items = rows.GetRange(0, limit);
        return new(items, EncodeCursor(keyOf(items[^1])));
    }
}
