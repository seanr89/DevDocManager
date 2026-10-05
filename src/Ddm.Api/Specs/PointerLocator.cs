using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Ddm.Api.Specs;

/// <summary>Maps a JSON pointer, as OpenAPI diagnostics report them, to a line and column in the source text.</summary>
public static partial class PointerLocator
{
    [GeneratedRegex(@"#/\S*")]
    private static partial Regex PointerInText();

    /// <summary>The pointer itself, or one quoted in the message when the library leaves the pointer empty.</summary>
    public static string? PointerFrom(string? pointer, string message)
    {
        if (!string.IsNullOrEmpty(pointer)) return pointer;
        var m = PointerInText().Match(message);
        return m.Success ? m.Value : null;
    }

    /// <summary>The 1-based position of the deepest node the pointer reaches, or null if not even its first segment exists.</summary>
    public static (int Line, int Column)? Locate(YamlNode root, string? pointer)
    {
        if (pointer is null) return null;
        var p = pointer.StartsWith('#') ? pointer[1..] : pointer;
        if (!p.StartsWith('/')) return null;

        var node = root;
        var found = false;
        foreach (var raw in p[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            YamlNode? next = node switch
            {
                YamlMappingNode map => map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode s && s.Value == segment).Value,
                YamlSequenceNode seq when int.TryParse(segment, out var i) && i >= 0 && i < seq.Children.Count => seq.Children[i],
                _ => null,
            };
            if (next is null) break;
            node = next;
            found = true;
        }
        return found ? ((int)node.Start.Line, (int)node.Start.Column) : null;
    }
}
