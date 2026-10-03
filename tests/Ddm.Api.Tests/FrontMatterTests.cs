using System.Diagnostics;
using Ddm.Api.Common;
using Ddm.Api.Documents;

namespace Ddm.Api.Tests;

public class FrontMatterTests
{
    private static JsonElement Json(ParsedMarkdown p) => JsonDocument.Parse(p.FrontMatterJson).RootElement;

    [Fact]
    public void Title_and_tags_come_from_front_matter()
    {
        var p = FrontMatter.Parse("---\ntitle: Setup\ntags: [onboarding, guide]\ndraft: true\nweight: 3\n---\n# Heading\nbody", "guides/setup.md");
        Assert.Equal("Setup", p.Title);
        Assert.Equal("# Heading\nbody", p.Body);
        Assert.Equal(["onboarding", "guide"], Json(p).GetProperty("tags").EnumerateArray().Select(e => e.GetString()));
        Assert.True(Json(p).GetProperty("draft").GetBoolean());
        Assert.Equal(3, Json(p).GetProperty("weight").GetInt32());
    }

    [Fact]
    public void Without_a_title_the_first_heading_is_used()
    {
        Assert.Equal("Hello World", FrontMatter.Parse("intro\n\n# Hello World\n\ntext", "a.md").Title);
    }

    [Fact]
    public void Headings_inside_code_fences_are_ignored()
    {
        Assert.Equal("setup", FrontMatter.Parse("```\n# not a title\n```\n", "guides/setup.md").Title);
    }

    [Fact]
    public void Without_anything_the_file_name_is_the_title()
    {
        Assert.Equal("setup", FrontMatter.Parse("no heading here", "guides/setup.md").Title);
        Assert.Equal("empty", FrontMatter.Parse("", "empty.md").Title);
    }

    [Fact]
    public void Crlf_and_a_byte_order_mark_are_handled()
    {
        var p = FrontMatter.Parse("\uFEFF---\r\ntitle: Win\r\n---\r\n# X\r\n", "a.md");
        Assert.Equal("Win", p.Title);
    }

    [Fact]
    public void Empty_front_matter_is_an_empty_object()
    {
        var p = FrontMatter.Parse("---\n---\nbody", "a.md");
        Assert.Equal("{}", p.FrontMatterJson);
        Assert.Equal("body", p.Body);
    }

    [Fact]
    public void A_long_title_is_truncated_to_300_characters()
    {
        Assert.Equal(300, FrontMatter.Parse($"---\ntitle: {new string('x', 500)}\n---\n", "a.md").Title.Length);
    }

    // Review Focus 3
    [Theory]
    [InlineData("---\nthis is just text with a rule above and no closing")]
    [InlineData("---\n# Looks like a heading, never closed\n")]
    public void An_unclosed_leading_rule_is_body_text_not_front_matter(string markdown)
    {
        var p = FrontMatter.Parse(markdown, "a.md");
        Assert.Equal(markdown, p.Body);
        Assert.Equal("{}", p.FrontMatterJson);
    }

    [Fact]
    public void Four_dashes_is_a_horizontal_rule_not_front_matter()
    {
        var p = FrontMatter.Parse("----\ntitle: no\n----\n", "a.md");
        Assert.Equal("{}", p.FrontMatterJson);
    }

    [Theory]
    [InlineData("---\ntitle: [unclosed\n---\n")]
    [InlineData("---\n- just\n- a list\n---\n")]
    [InlineData("---\nplain scalar\n---\n")]
    public void Malformed_or_non_mapping_front_matter_is_a_400(string markdown)
    {
        var ex = Assert.Throws<ApiException>(() => FrontMatter.Parse(markdown, "a.md"));
        Assert.Equal(400, ex.Status);
        Assert.Equal("invalid_front_matter", ex.Code);
    }

    // Review Focus 3
    [Fact]
    public void A_yaml_alias_bomb_is_rejected_quickly()
    {
        var yaml = new StringBuilder("---\na: &a [x,x,x,x,x,x,x,x,x]\n");
        var prev = "a";
        for (var i = 0; i < 9; i++)
        {
            var name = $"l{i}";
            yaml.Append($"{name}: &{name} [{string.Join(',', Enumerable.Repeat('*' + prev, 9))}]\n");
            prev = name;
        }
        yaml.Append("---\n");

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<ApiException>(() => FrontMatter.Parse(yaml.ToString(), "a.md"));
        Assert.Equal("invalid_front_matter", ex.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    // Review Focus 3
    [Fact]
    public void Deeply_nested_yaml_is_rejected_without_crashing()
    {
        var nested = new string('[', 3000) + new string(']', 3000);
        var ex = Assert.Throws<ApiException>(() => FrontMatter.Parse($"---\na: {nested}\n---\n", "a.md"));
        Assert.Equal("invalid_front_matter", ex.Code);
    }

    [Fact]
    public void Oversized_front_matter_is_rejected()
    {
        var big = "---\n" + string.Concat(Enumerable.Range(0, 3000).Select(i => $"key{i}: value{i}\n")) + "---\n";
        Assert.Equal("invalid_front_matter", Assert.Throws<ApiException>(() => FrontMatter.Parse(big, "a.md")).Code);
    }
}
