using Ddm.Api.Assets;
using Ddm.Api.Common;
using Ganss.Xss;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Ddm.Api.Documents;

public sealed class MarkdownRenderer
{
    public const string MissingAssetClass = "ddm-missing-asset";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAlertBlocks()
        .Build();

    /// <summary>Renders markdown to HTML, then sanitizes against an allow-list. Raw HTML in the source is untrusted.</summary>
    public string ToHtml(string markdown) => Sanitize(Markdown.ToHtml(markdown, Pipeline));

    /// <summary>Project asset paths that <paramref name="markdown"/> uses as images, resolved against the document's folder.</summary>
    public IReadOnlySet<string> ImagePaths(string markdown, string documentPath) =>
        Images(Markdown.Parse(markdown, Pipeline))
            .Select(image => ResolveAsset(documentPath, image.Url))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Renders with relative images pointed at <paramref name="assetUrls"/> (keyed by resolved asset path).
    /// A relative image with no URL keeps its src and gains <see cref="MissingAssetClass"/>; absolute URLs are untouched.
    /// </summary>
    public string ToHtml(string markdown, string documentPath, IReadOnlyDictionary<string, string> assetUrls)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        foreach (var image in Images(doc))
        {
            if (!IsRelative(image.Url)) continue;
            if (ResolveAsset(documentPath, image.Url) is { } path && assetUrls.TryGetValue(path, out var url)) image.Url = url;
            else image.GetAttributes().AddClass(MissingAssetClass);
        }
        return Sanitize(doc.ToHtml(Pipeline));
    }

    private static List<LinkInline> Images(MarkdownDocument doc) =>
        doc.Descendants<LinkInline>().Where(l => l.IsImage && !string.IsNullOrEmpty(l.Url)).ToList();

    private static bool IsRelative(string? url) => url is { Length: > 0 } && !url.StartsWith('/') && !url.Contains(':');

    private static string? ResolveAsset(string documentPath, string? url) =>
        url is not null && ContentPath.Resolve(documentPath, url) is { } path && AssetPath.IsValid(path) ? path : null;

    private static string Sanitize(string html)
    {
        var sanitizer = new HtmlSanitizer(); // a fresh instance per call: sanitizers are not safe to share once configured
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        return sanitizer.Sanitize(html);
    }
}
