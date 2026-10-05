using System.Text.Json;
using System.Text.RegularExpressions;
using Ddm.Api.Common;

namespace Ddm.Api.Tags;

public static partial class TagName
{
    public const int MaxLength = 50;
    public const int MaxPerItem = 20;

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,49}\z")]
    private static partial Regex Valid();

    /// <summary>Trims, lowercases and turns runs of whitespace or '_' into '-'; 400 invalid_tag if the result is not a valid name.</summary>
    public static string Normalize(string? raw)
    {
        var name = Separators().Replace((raw ?? "").Trim().ToLowerInvariant(), "-");
        return Valid().IsMatch(name)
            ? name
            : throw ApiException.BadRequest("invalid_tag", "Invalid tag",
                $"'{raw}' is not a valid tag: use 1-{MaxLength} lowercase letters, digits and '-', starting with a letter or digit");
    }

    /// <summary>Normalises, de-duplicates and orders a tag set; 400 too_many_tags above the per-item limit.</summary>
    public static IReadOnlyList<string> NormalizeSet(IEnumerable<string?> raw)
    {
        var set = raw.Select(Normalize).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return set.Count <= MaxPerItem
            ? set
            : throw ApiException.BadRequest("too_many_tags", "Too many tags", $"An item can have at most {MaxPerItem} tags");
    }

    /// <summary>Repeated <c>?tag=</c> query values, normalised. All of them must match (AND).</summary>
    public static IReadOnlyList<string> Filter(string[]? raw) => raw is null || raw.Length == 0 ? [] : NormalizeSet(raw);

    public static bool DeclaredInFrontMatter(string frontMatterJson)
    {
        using var doc = JsonDocument.Parse(frontMatterJson);
        return doc.RootElement.TryGetProperty("tags", out _);
    }

    /// <summary>The tags a document's front matter declares, or null when it has no <c>tags</c> key.</summary>
    public static IReadOnlyList<string>? FromFrontMatter(string frontMatterJson)
    {
        using var doc = JsonDocument.Parse(frontMatterJson);
        if (!doc.RootElement.TryGetProperty("tags", out var tags)) return null;
        return tags.ValueKind switch
        {
            JsonValueKind.Null => Array.Empty<string>(),
            JsonValueKind.Array => NormalizeSet(tags.EnumerateArray().Select(Scalar).ToList()),
            _ => NormalizeSet([Scalar(tags)]),
        };
    }

    // YAML turns `tags: [v2, 2024]` into a string and a number; both are fine tag names.
    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        _ => throw ApiException.BadRequest("invalid_tag", "Invalid tag", "Front matter 'tags' must be a string or a list of strings"),
    };
}
