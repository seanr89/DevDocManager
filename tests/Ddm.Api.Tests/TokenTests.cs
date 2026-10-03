using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Identity;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;
using Ddm.Api.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class TokenTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Created_token_shows_its_secret_once_and_is_stored_hashed()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/tokens", new { name = "ci", scope = "write" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var created = await r.Content.ReadFromJsonAsync<JsonElement>();
        var secret = created.GetProperty("secret").GetString()!;
        Assert.StartsWith("ddm_tok_", secret);

        var list = await alice.GetStringAsync("/api/v1/projects/p/tokens");
        Assert.DoesNotContain(secret, list);
        Assert.DoesNotContain("secret", list, StringComparison.OrdinalIgnoreCase);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
        var row = await db.ApiTokens.SingleAsync();
        ApiTokenSecrets.TryParse(secret, out _, out var raw);
        Assert.Equal(ApiTokenSecrets.Hash(raw), row.HashedSecret);
        Assert.DoesNotContain(raw, row.HashedSecret);
    }

    [Fact]
    public async Task Token_authenticates_as_a_token_caller_bound_to_its_project()
    {
        var alice = ClientFor("alice");
        var p = await CreateProjectAsync(alice, "p");
        var me = await TokenClient(await CreateTokenAsync(alice, "p", "read")).GetFromJsonAsync<MeDto>("/api/v1/me");
        Assert.Equal("token", me!.Kind);
        Assert.Equal("read", me.Scope);
        Assert.Null(me.UserId);
        Assert.StartsWith("token:", me.Actor);
        Assert.NotNull(me.ProjectId);
    }

    // Review Focus 5: a token for project A sees nothing of project B.
    [Fact]
    public async Task Token_cannot_see_other_projects_even_internal_ones()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "a");
        await CreateProjectAsync(alice, "b", "internal");
        var token = TokenClient(await CreateTokenAsync(alice, "a", "write"));

        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/v1/projects/a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync("/api/v1/projects/b")).StatusCode);
        var list = await ReadAsync<Page<ProjectDto>>(await token.GetAsync("/api/v1/projects"));
        Assert.Equal(["a"], list.Items.Select(x => x.Slug));
    }

    [Fact]
    public async Task Tokens_never_administer_and_cannot_create_projects()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var token = TokenClient(await CreateTokenAsync(alice, "p", "write"));

        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/v1/projects/p/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PutAsJsonAsync("/api/v1/projects/p/members/x", new { role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.DeleteAsync("/api/v1/projects/p")).StatusCode);
        var create = await token.PostAsJsonAsync("/api/v1/projects", new { slug = "new", name = "New" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("user_required", await ProblemCodeAsync(create));
    }

    [Fact]
    public async Task Revoked_token_stops_working()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var secret = await CreateTokenAsync(alice, "p", "read");
        var id = (await ReadAsync<Page<TokenDto>>(await alice.GetAsync("/api/v1/projects/p/tokens"))).Items.Single().Id;

        Assert.Equal(HttpStatusCode.OK, (await TokenClient(secret).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/projects/p/tokens/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/projects/p/tokens/{id}")).StatusCode); // idempotent
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenClient(secret).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var secret = await CreateTokenAsync(alice, "p", "read");
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
            await db.ApiTokens.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenClient(secret).GetAsync("/api/v1/me")).StatusCode);
    }

    [Theory]
    [InlineData("ddm_tok_garbage")]
    [InlineData("ddm_tok_00000000000000000000000000000000_wrong")]
    public async Task Malformed_or_unknown_tokens_get_401_not_500(string secret)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenClient(secret).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Right_id_wrong_secret_is_rejected()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var secret = await CreateTokenAsync(alice, "p", "read");
        ApiTokenSecrets.TryParse(secret, out var id, out _);
        var forged = $"ddm_tok_{id:N}_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenClient(forged).GetAsync("/api/v1/me")).StatusCode);
    }

    [Theory]
    [InlineData("", "read")] [InlineData("ci", "admin")] [InlineData("ci", "")] [InlineData("ci", "3")]
    public async Task Create_validates_name_and_scope(string name, string scope)
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/tokens", new { name, scope });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_an_expiry_in_the_past()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/tokens",
            new { name = "ci", scope = "read", expiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Revoking_an_unknown_token_returns_404()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.DeleteAsync($"/api/v1/projects/p/tokens/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("token_not_found", await ProblemCodeAsync(r));
    }
}
