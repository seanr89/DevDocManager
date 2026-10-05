using Ddm.Api.Assets;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class ContentEndpointTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private ContentUrlSigner Signer => Factory.Services.GetRequiredService<ContentUrlSigner>();

    private async Task<(HttpClient Alice, Guid ProjectId)> WithAssetAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutAssetAsync(alice, "p", "images/a.png", PngBytes);
        return (alice, await ProjectIdAsync("p"));
    }

    [Fact]
    public async Task A_signed_link_serves_the_asset_without_credentials()
    {
        var (_, pid) = await WithAssetAsync();
        var url = Signer.UrlFor(pid, Sha(PngBytes));
        Assert.StartsWith("/content/", url); // Testing has no Content:BaseUrl, so links are same-origin
        var r = await Anonymous().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(PngBytes, await r.Content.ReadAsByteArrayAsync());
        Assert.True(r.Headers.CacheControl!.Private);
        Assert.InRange(r.Headers.CacheControl.MaxAge!.Value, TimeSpan.FromHours(23), TimeSpan.FromHours(48));
        Assert.Equal(AssetResponses.Csp, r.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task A_tampered_link_is_403_invalid_signature()
    {
        var (_, pid) = await WithAssetAsync();
        var url = Signer.UrlFor(pid, Sha(PngBytes)) + "x";
        var r = await Anonymous().GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("invalid_signature", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task An_expired_link_is_403_signature_expired()
    {
        var (_, pid) = await WithAssetAsync();
        var sha = Sha(PngBytes);
        var past = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
        var r = await Anonymous().GetAsync($"/content/{pid:N}/{sha}?exp={past}&sig={Signer.Sign(pid, sha, past)}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("signature_expired", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task A_valid_signature_cannot_reach_another_projects_content()
    {
        var (alice, _) = await WithAssetAsync();
        await CreateProjectAsync(alice, "q");
        var r = await Anonymous().GetAsync(Signer.UrlFor(await ProjectIdAsync("q"), Sha(PngBytes)));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Tombstoned_assets_are_still_served_to_old_links()
    {
        var (alice, pid) = await WithAssetAsync();
        await alice.DeleteAsync("/api/v1/projects/p/assets/images/a.png");
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().GetAsync(Signer.UrlFor(pid, Sha(PngBytes)))).StatusCode);
    }

    [Fact]
    public async Task The_app_refuses_to_start_without_a_content_signing_key()
    {
        await using var factory = new DdmApiFactory("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p", migrate: false,
            settings: new Dictionary<string, string?> { ["Content:SigningKey"] = "" });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Content:SigningKey", ex.ToString());
    }
}
