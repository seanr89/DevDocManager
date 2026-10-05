using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ddm.Api.Assets;
using Ddm.Api.Domain;

namespace Ddm.Api.Publishing;

public enum PlanAction { Create, Update, Unchanged, Delete }

/// <summary>Identifies an item in a project: its path, or its name for a spec.</summary>
public sealed record ItemKey(ItemType Type, string Key);

public sealed record PlanStep(ItemType Type, string Key, PlanAction Action);

public sealed record PublishPlan(IReadOnlyList<PlanStep> Steps, int InScope)
{
    public int Count(PlanAction action) => Steps.Count(s => s.Action == action);

    /// <summary>Deleting everything in scope, or more than half of a scope of at least 10 items, needs explicit consent.</summary>
    public bool IsMassDelete
    {
        get
        {
            var deletes = Count(PlanAction.Delete);
            return deletes > 0 && (deletes == InScope || (InScope >= 10 && deletes * 2 > InScope));
        }
    }

    public bool SameAs(PublishPlan other) => InScope == other.InScope && Steps.SequenceEqual(other.Steps);
}

public static partial class PublishPlanner
{
    /// <param name="wanted">What the archive holds, keyed by item, valued by content SHA-256.</param>
    /// <param name="live">The project's live items (tombstones excluded), valued by their current SHA-256.</param>
    /// <param name="prefix">The publish scope: documents and assets under it; specs only when it is empty.</param>
    public static PublishPlan Plan(IReadOnlyDictionary<ItemKey, string> wanted, IReadOnlyDictionary<ItemKey, string> live, string prefix)
    {
        bool InScope(ItemKey k) => k.Type == ItemType.Spec ? prefix.Length == 0 : k.Key.StartsWith(prefix, StringComparison.Ordinal);

        var steps = new List<PlanStep>();
        foreach (var (key, sha) in wanted)
        {
            var action = !live.TryGetValue(key, out var current) ? PlanAction.Create
                : current == sha ? PlanAction.Unchanged
                : PlanAction.Update;
            steps.Add(new(key.Type, key.Key, action));
        }
        foreach (var key in live.Keys.Where(k => InScope(k) && !wanted.ContainsKey(k)))
            steps.Add(new(key.Type, key.Key, PlanAction.Delete));

        steps.Sort((a, b) => a.Type != b.Type ? a.Type.CompareTo(b.Type) : string.CompareOrdinal(a.Key, b.Key));
        return new(steps, live.Keys.Count(InScope));
    }

    [GeneratedRegex(@"^[""']?openapi[""']?[ \t]*:", RegexOptions.Multiline)]
    private static partial Regex TopLevelYamlOpenApiKey();

    [GeneratedRegex(@"^\s*\{\s*""openapi""\s*:")]
    private static partial Regex LeadingJsonOpenApiKey();

    /// <summary>
    /// What an archive file becomes: markdown is a document; YAML or JSON with a top-level openapi key is a spec;
    /// any other allow-listed type is an asset; anything else is null (unsupported).
    /// </summary>
    public static ItemType? Classify(string path, byte[] content)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".md") return ItemType.Document;
        if (ext is ".yaml" or ".yml" or ".json" && LooksLikeSpec(ext, content)) return ItemType.Spec;
        return AssetTypes.ForPath(path) is not null ? ItemType.Asset : null;
    }

    /// <summary>A spec's name is its file stem, lowercased: apis/Payments.v2.yaml → payments.v2.</summary>
    public static string SpecNameFor(string path) => System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

    private static bool LooksLikeSpec(string ext, byte[] content)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { return false; }
        if (ext != ".json") return TopLevelYamlOpenApiKey().IsMatch(text);
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("openapi", out _);
        }
        catch (JsonException)
        {
            // Broken JSON that starts like a spec is still a spec, so validation reports its errors instead of storing it as an asset.
            return LeadingJsonOpenApiKey().IsMatch(text);
        }
    }
}
