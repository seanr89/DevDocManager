using Microsoft.Net.Http.Headers;

namespace Ddm.Api.Documents;

public enum DocFormat { Markdown, Html, Json }

public static class ContentNegotiation
{
    /// <summary>Returns null when nothing the client accepts is available (a 406).</summary>
    public static DocFormat? Choose(IList<MediaTypeHeaderValue> accept)
    {
        if (accept.Count == 0) return DocFormat.Markdown;
        var ordered = accept.Select((m, i) => (Media: m, Index: i))
            .OrderByDescending(x => x.Media.Quality ?? 1.0).ThenBy(x => x.Index);
        foreach (var (media, _) in ordered)
        {
            if ((media.Quality ?? 1.0) <= 0) continue;
            switch (media.MediaType.Value?.ToLowerInvariant())
            {
                case "text/markdown": return DocFormat.Markdown;
                case "text/html": return DocFormat.Html;
                case "application/json": return DocFormat.Json;
                case "text/*" or "*/*": return DocFormat.Markdown;
            }
        }
        return null;
    }
}
