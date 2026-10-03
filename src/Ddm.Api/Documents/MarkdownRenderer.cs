using Ganss.Xss;
using Markdig;

namespace Ddm.Api.Documents;

public sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAlertBlocks()
        .Build();

    /// <summary>Renders markdown to HTML, then sanitizes against an allow-list. Raw HTML in the source is untrusted.</summary>
    public string ToHtml(string markdown)
    {
        var sanitizer = new HtmlSanitizer(); // a fresh instance per call: sanitizers are not safe to share once configured
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        return sanitizer.Sanitize(Markdown.ToHtml(markdown, Pipeline));
    }
}
