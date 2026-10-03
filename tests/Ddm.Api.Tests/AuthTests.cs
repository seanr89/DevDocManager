using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class AuthTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private static async Task<string> CodeAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    [Fact]
    public async Task Missing_credentials_return_401_problem()
    {
        var response = await Anonymous().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await CodeAsync(response));
    }

    [Fact]
    public async Task Valid_user_token_identifies_the_caller()
    {
        var response = await ClientFor("alice").GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user", me.GetProperty("kind").GetString());
        Assert.Equal("alice", me.GetProperty("userId").GetString());
        Assert.Equal("user:alice", me.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestAuth.ExpiredTokenFor("alice"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestAuth.TokenFor("mallory", key: "some-other-signing-key-0123456789abcdef"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Token_for_another_audience_is_rejected()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestAuth.TokenFor("alice", audience: "someone-else"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Healthz_stays_anonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Production_refuses_to_start_with_only_the_dev_signing_key()
    {
        await using var factory = new DdmApiFactory("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p", migrate: false, environment: "Production");
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Auth:Authority", ex.ToString());
    }
}
