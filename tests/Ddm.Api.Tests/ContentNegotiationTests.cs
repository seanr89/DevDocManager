using Ddm.Api.Documents;
using Microsoft.Net.Http.Headers;

namespace Ddm.Api.Tests;

public class ContentNegotiationTests
{
    private static DocFormat? Choose(params string[] accept) =>
        ContentNegotiation.Choose(accept.Length == 0 ? [] : MediaTypeHeaderValue.ParseList(accept));

    [Fact] public void No_accept_header_means_markdown() => Assert.Equal(DocFormat.Markdown, Choose());
    [Fact] public void Wildcard_means_markdown() => Assert.Equal(DocFormat.Markdown, Choose("*/*"));
    [Fact] public void Html_is_chosen_when_asked_for() => Assert.Equal(DocFormat.Html, Choose("text/html"));
    [Fact] public void Json_is_chosen_when_asked_for() => Assert.Equal(DocFormat.Json, Choose("application/json"));
    [Fact] public void Quality_values_decide() => Assert.Equal(DocFormat.Html, Choose("text/markdown;q=0.5, text/html"));
    [Fact] public void Listed_order_breaks_ties() => Assert.Equal(DocFormat.Markdown, Choose("text/markdown, text/html"));
    [Fact] public void Unsupported_types_are_not_acceptable() => Assert.Null(Choose("application/xml"));
    [Fact] public void Zero_quality_excludes_a_type() => Assert.Null(Choose("text/html;q=0"));
    [Fact] public void Falls_through_to_a_supported_type() => Assert.Equal(DocFormat.Html, Choose("application/xml, text/html;q=0.1"));
}
