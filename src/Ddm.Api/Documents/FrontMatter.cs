using System.Text;
using System.Text.Json;
using Ddm.Api.Common;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Ddm.Api.Documents;

public sealed record ParsedMarkdown(string Title, string FrontMatterJson, string Body);

public static class FrontMatter
{
    public const int MaxBytes = 16 * 1024;
    private const int MaxDepth = 20;
    private const int MaxTitle = 300;

    public static ParsedMarkdown Parse(string markdown, string path)
    {
        var (yaml, body) = Split(markdown.TrimStart('\uFEFF'));
        var json = yaml is null ? "{}" : ToJson(yaml);
        var title = TitleFrom(json) ?? FirstHeading(body) ?? System.IO.Path.GetFileNameWithoutExtension(path);
        return new(title.Length <= MaxTitle ? title : title[..MaxTitle], json, body);
    }

    /// <summary>A leading "---" line opens front matter only if a closing "---" line follows.</summary>
    private static (string? Yaml, string Body) Split(string text)
    {
        using var reader = new StringReader(text);
        if (reader.ReadLine()?.TrimEnd() != "---") return (null, text);
        var yaml = new StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (line.TrimEnd() == "---") return (yaml.ToString(), reader.ReadToEnd());
            yaml.AppendLine(line);
        }
        return (null, text);
    }

    private static string ToJson(string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > MaxBytes) throw Invalid($"Front matter is limited to {MaxBytes} bytes");
        if (string.IsNullOrWhiteSpace(yaml)) return "{}";
        try
        {
            // Reject aliases (billion-laughs) and deep nesting before anything recurses over the document.
            var parser = new Parser(new StringReader(yaml));
            var depth = 0;
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case AnchorAlias: throw Invalid("YAML aliases are not supported in front matter");
                    case SequenceStart or MappingStart when ++depth > MaxDepth: throw Invalid($"Front matter is nested deeper than {MaxDepth} levels");
                    case SequenceEnd or MappingEnd: depth--; break;
                }
            }

            var value = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object?>(yaml);
            if (value is null) return "{}";
            if (value is not System.Collections.IDictionary) throw Invalid("Front matter must be a YAML mapping (key: value pairs)");

            var json = new SerializerBuilder().JsonCompatible().Build().Serialize(value).Trim();
            using (JsonDocument.Parse(json)) { }
            return json;
        }
        catch (YamlException ex) { throw Invalid(ex.Message); }
        catch (JsonException) { throw Invalid("Front matter could not be represented as JSON"); }
    }

    private static string? TitleFrom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(t.GetString())
            ? t.GetString()!.Trim()
            : null;
    }

    private static string? FirstHeading(string body)
    {
        var inFence = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("```") || line.StartsWith("~~~")) inFence = !inFence;
            else if (!inFence && line.StartsWith("# ") && line[2..].Trim() is { Length: > 0 } heading) return heading;
        }
        return null;
    }

    private static ApiException Invalid(string detail) =>
        ApiException.BadRequest("invalid_front_matter", "Invalid front matter", detail);
}
