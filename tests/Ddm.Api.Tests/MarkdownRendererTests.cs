using Ddm.Api.Documents;

namespace Ddm.Api.Tests;

public class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new();

    [Fact]
    public void Renders_headings_with_anchor_ids_tables_and_code_fences()
    {
        var html = Renderer.ToHtml("# Title\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```csharp\nvar x = 1;\n```\n");
        Assert.Contains("<h1", html);
        Assert.Contains("id=\"title\"", html);
        Assert.Contains("<table", html);
        Assert.Contains("<code", html);
    }

    [Fact]
    public void Renders_admonition_style_alerts()
    {
        Assert.Contains("markdown-alert", Renderer.ToHtml("> [!NOTE]\n> Remember this\n"));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("<img src=x onerror=alert(1)>", "onerror")]
    [InlineData("[click](javascript:alert(1))", "javascript:")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "<iframe")]
    [InlineData("<div onclick=\"x()\">hi</div>", "onclick")]
    [InlineData("![x](data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+)", "data:")]
    [InlineData("<svg onload=alert(1)></svg>", "onload")]
    public void Dangerous_content_is_removed(string markdown, string forbidden) =>
        Assert.DoesNotContain(forbidden, Renderer.ToHtml(markdown), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Safe_links_survive() =>
        Assert.Contains("href=\"https://example.com\"", Renderer.ToHtml("[ok](https://example.com)"));

    [Fact]
    public void Image_paths_are_resolved_against_the_document_folder()
    {
        var paths = Renderer.ImagePaths(
            "![a](img/a.png) ![b](../logo.svg) ![c](https://x.example/c.png) ![d](../../escape.png) ![e](notes.md)", "guides/setup.md");
        Assert.True(paths.SetEquals(["guides/img/a.png", "logo.svg"]));
    }

    [Fact]
    public void Resolved_images_get_their_urls_and_missing_ones_are_marked()
    {
        var html = Renderer.ToHtml("![a](img/a.png) ![b](img/missing.png) ![c](https://x.example/c.png)", "guides/setup.md",
            new Dictionary<string, string> { ["guides/img/a.png"] = "https://content.example/content/p/abc?exp=1&sig=s" });
        Assert.Contains("src=\"https://content.example/content/p/abc?exp=1&amp;sig=s\"", html);
        Assert.Contains("class=\"ddm-missing-asset\"", html);
        Assert.Contains("src=\"https://x.example/c.png\"", html);
    }

    [Fact]
    public void Resolution_does_not_weaken_sanitizing()
    {
        var html = Renderer.ToHtml("![x](data:image/svg+xml;base64,PHN2Zz4=) <script>alert(1)</script>", "a.md",
            new Dictionary<string, string>());
        Assert.DoesNotContain("data:", html);
        Assert.DoesNotContain("<script", html);
    }
}
