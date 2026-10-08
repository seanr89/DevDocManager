using Ddm.Api.Specs;
using YamlDotNet.RepresentationModel;

namespace Ddm.Api.Tests;

public class PointerLocatorTests
{
    private static YamlNode Load(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents[0].RootNode;
    }

    [Fact]
    public void Locates_a_nested_mapping_value()
    {
        var root = Load("openapi: 3.0.3\npaths:\n  /pets:\n    get:\n      responses: {}\n");
        Assert.Equal((5, 18), PointerLocator.Locate(root, "#/paths/~1pets/get/responses"));
    }

    [Fact]
    public void Locates_sequence_items_in_json()
    {
        var root = Load("{\n  \"tags\": [\n    {\"name\": \"a\"},\n    {\"name\": \"b\"}\n  ]\n}");
        Assert.Equal(4, PointerLocator.Locate(root, "/tags/1")!.Value.Line);
    }

    [Fact]
    public void A_missing_segment_falls_back_to_the_deepest_existing_node()
    {
        var root = Load("info:\n  version: '1'\n");
        Assert.Equal(2, PointerLocator.Locate(root, "#/info/title")!.Value.Line);
    }

    [Fact] public void Unknown_pointers_are_null() => Assert.Null(PointerLocator.Locate(Load("a: 1\n"), "#/zzz"));

    [Fact]
    public void Pointers_quoted_in_messages_are_extracted() =>
        Assert.Equal("#/paths", PointerLocator.PointerFrom("", "nope is not a valid property at #/paths"));

    [Fact] public void A_given_pointer_wins() => Assert.Equal("#/a", PointerLocator.PointerFrom("#/a", "at #/b"));
}
