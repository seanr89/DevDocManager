using System.Text.RegularExpressions;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class DocumentImageTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private static string ImageSrc(string html) =>
        WebUtility.HtmlDecode(Regex.Match(html, "src=\"(/content/[^\"]+)\"").Groups[1].Value);

    private static async Task<string> HtmlAsync(HttpClient c, string path) =>
        await (await GetDocAsync(c, "p", path, "text/html")).Content.ReadAsStringAsync();

    // Review Focus 5
    [Fact]
    public async Task Rendered_images_point_at_signed_links_that_load_without_credentials()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutAssetAsync(alice, "p", "images/flow.png", PngBytes);
        await PutDocAsync(alice, "p", "guides/deep/setup.md", "# Setup\n\n![flow](../../images/flow.png)\n\n![gone](missing.png)\n");

        var html = await HtmlAsync(alice, "guides/deep/setup.md");
        var src = ImageSrc(html);
        Assert.NotEmpty(src);
        var image = await Anonymous().GetAsync(src);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(PngBytes, await image.Content.ReadAsByteArrayAsync());
        Assert.Contains("ddm-missing-asset", html);
    }

    // Review Focus 5
    [Fact]
    public async Task A_replaced_image_gets_a_new_url()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        await PutDocAsync(alice, "p", "index.md", "![a](a.png)");
        var before = ImageSrc(await HtmlAsync(alice, "index.md"));

        await PutAssetAsync(alice, "p", "a.png", PngVariant(1), ifMatch: $"\"{Sha(PngBytes)}\"");
        var after = ImageSrc(await HtmlAsync(alice, "index.md"));
        Assert.NotEqual(before, after);
        Assert.Equal(PngVariant(1), await (await Anonymous().GetAsync(after)).Content.ReadAsByteArrayAsync());
    }
}
