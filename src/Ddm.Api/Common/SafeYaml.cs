using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Ddm.Api.Common;

public sealed record YamlProblem(string Message, int? Line, int? Column);

/// <summary>
/// Checks YAML (and JSON, which YAML parses) before anything materialises it: aliases enable billion-laughs
/// expansion and deep nesting makes recursive consumers overflow, so both are refused up front.
/// </summary>
public static class SafeYaml
{
    public static YamlProblem? Check(string yaml, int maxDepth)
    {
        var last = new Mark();
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var depth = 0;
            while (parser.MoveNext())
            {
                var e = parser.Current!;
                last = e.End;
                switch (e)
                {
                    case AnchorAlias:
                        return At(e.Start, "YAML aliases are not supported");
                    case SequenceStart or MappingStart when ++depth > maxDepth:
                        return At(e.Start, $"YAML is nested deeper than {maxDepth} levels");
                    case SequenceEnd or MappingEnd:
                        depth--;
                        break;
                }
            }
            return null;
        }
        catch (YamlException ex) { return At(ex.Start, ex.Message); }
        // YamlDotNet's scanner throws a bare InvalidOperationException (no position) for some unterminated flow
        // collections, so report the end of the last event it did read.
        catch (InvalidOperationException) { return At(last, "YAML syntax error: unterminated or malformed flow collection"); }
    }

    private static YamlProblem At(Mark mark, string message) =>
        mark.Line > 0 ? new(message, (int)mark.Line, (int)mark.Column) : new(message, null, null);
}
