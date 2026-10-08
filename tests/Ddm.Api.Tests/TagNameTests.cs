using Ddm.Api.Common;
using Ddm.Api.Tags;

namespace Ddm.Api.Tests;

public class TagNameTests
{
    [Theory]
    [InlineData("guide", "guide")] [InlineData("  Getting Started ", "getting-started")]
    [InlineData("snake_case", "snake-case")] [InlineData("V2", "v2")] [InlineData("a  _ b", "a-b")]
    public void Normalizes(string raw, string expected) => Assert.Equal(expected, TagName.Normalize(raw));

    [Theory] [InlineData("")] [InlineData("-lead")] [InlineData("emoji✓")] [InlineData("a/b")]
    public void Rejects(string raw) => Assert.Equal("invalid_tag", Assert.Throws<ApiException>(() => TagName.Normalize(raw)).Code);

    [Fact] public void Rejects_names_longer_than_50() =>
        Assert.Equal("invalid_tag", Assert.Throws<ApiException>(() => TagName.Normalize(new string('a', 51))).Code);

    [Fact] public void Sets_are_deduplicated_and_ordered() => Assert.Equal(["a", "b"], TagName.NormalizeSet(["b", "A", "a "]));

    [Fact]
    public void More_than_20_tags_is_rejected() =>
        Assert.Equal("too_many_tags",
            Assert.Throws<ApiException>(() => TagName.NormalizeSet(Enumerable.Range(0, 21).Select(i => (string?)$"t{i}"))).Code);

    [Fact] public void An_absent_filter_is_empty() => Assert.Empty(TagName.Filter(null));

    [Fact] public void Front_matter_without_tags_is_null() => Assert.Null(TagName.FromFrontMatter("{\"title\":\"x\"}"));
    [Fact] public void Front_matter_list() => Assert.Equal(["2024", "guide"], TagName.FromFrontMatter("{\"tags\":[\"guide\",2024]}"));
    [Fact] public void Front_matter_single_string() => Assert.Equal(["guide"], TagName.FromFrontMatter("{\"tags\":\"Guide\"}"));
    [Fact] public void Front_matter_null_is_an_empty_set() => Assert.Empty(TagName.FromFrontMatter("{\"tags\":null}")!);

    [Fact] public void Front_matter_objects_are_rejected() =>
        Assert.Equal("invalid_tag", Assert.Throws<ApiException>(() => TagName.FromFrontMatter("{\"tags\":{\"a\":1}}")).Code);

    [Fact] public void Declared_in_front_matter_only_checks_the_key() =>
        Assert.True(TagName.DeclaredInFrontMatter("{\"tags\":{\"a\":1}}"));
}
