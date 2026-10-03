using Ddm.Api.Common;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class ProjectTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Create_returns_201_and_makes_the_creator_an_admin()
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug = "payments", name = "Payments", description = "Pay docs" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("/api/v1/projects/payments", r.Headers.Location!.ToString());
        var p = await ReadAsync<ProjectDto>(r);
        Assert.Equal("admin", p.Role);
        Assert.Equal("private", p.Visibility);
        Assert.Equal("Pay docs", p.Description);
    }

    [Theory]
    [InlineData("")] [InlineData("Payments")] [InlineData("has space")] [InlineData("under_score")]
    [InlineData("-lead")] [InlineData("trail-")] [InlineData("a/b")]
    public async Task Create_rejects_invalid_slugs(string slug)
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug, name = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("validation_failed", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Create_rejects_a_slug_longer_than_64_characters()
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug = new string('a', 65), name = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_a_blank_name()
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug = "ok", name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory] [InlineData("public")] [InlineData("7")] [InlineData("1")] [InlineData("")]
    public async Task Create_rejects_unknown_visibility(string visibility)
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug = "ok", name = "X", visibility });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_without_a_body_returns_400()
    {
        var r = await ClientFor("alice").PostAsync("/api/v1/projects", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_with_malformed_json_returns_400_not_500()
    {
        var r = await ClientFor("alice").PostAsync("/api/v1/projects", new StringContent("{nope", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Duplicate_slug_returns_409()
    {
        await CreateProjectAsync(ClientFor("alice"), "payments");
        var r = await ClientFor("bob").PostAsJsonAsync("/api/v1/projects", new { slug = "payments", name = "Mine" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("slug_taken", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync("/api/v1/projects")).StatusCode);
    }

    [Fact]
    public async Task List_returns_only_projects_the_caller_can_see()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "secret", "private");
        await CreateProjectAsync(alice, "shared", "internal");

        var bob = await ReadAsync<Page<ProjectDto>>(await ClientFor("bob").GetAsync("/api/v1/projects"));
        Assert.Equal(["shared"], bob.Items.Select(p => p.Slug));
        Assert.Equal("reader", bob.Items[0].Role);

        var aliceList = await ReadAsync<Page<ProjectDto>>(await alice.GetAsync("/api/v1/projects"));
        Assert.Equal(["secret", "shared"], aliceList.Items.Select(p => p.Slug));
    }

    [Fact]
    public async Task List_pages_through_every_project_exactly_once()
    {
        var alice = ClientFor("alice");
        foreach (var s in new[] { "p1", "p2", "p3", "p4", "p5" }) await CreateProjectAsync(alice, s);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var url = "/api/v1/projects?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await ReadAsync<Page<ProjectDto>>(await alice.GetAsync(url));
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(p => p.Slug));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(["p1", "p2", "p3", "p4", "p5"], seen);
    }

    [Theory] [InlineData("0")] [InlineData("-1")]
    public async Task List_rejects_non_positive_limit(string limit)
    {
        var r = await ClientFor("alice").GetAsync($"/api/v1/projects?limit={limit}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_limit", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task List_clamps_an_oversized_limit()
    {
        Assert.Equal(HttpStatusCode.OK, (await ClientFor("alice").GetAsync("/api/v1/projects?limit=100000")).StatusCode);
    }

    [Fact]
    public async Task List_rejects_a_malformed_cursor()
    {
        var r = await ClientFor("alice").GetAsync("/api/v1/projects?cursor=%21%21%21");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_cursor", await ProblemCodeAsync(r));
    }

    // Review Focus 5: no existence leak.
    [Fact]
    public async Task A_private_project_looks_exactly_like_a_missing_one_to_non_members()
    {
        await CreateProjectAsync(ClientFor("alice"), "secret", "private");
        var bob = ClientFor("bob");
        var hidden = await bob.GetAsync("/api/v1/projects/secret");
        var missing = await bob.GetAsync("/api/v1/projects/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(missing.StatusCode, hidden.StatusCode);
        Assert.Equal("project_not_found", await ProblemCodeAsync(hidden));
        Assert.Equal("project_not_found", await ProblemCodeAsync(missing));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync("/api/v1/projects/secret")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PatchAsJsonAsync("/api/v1/projects/secret", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Internal_project_is_readable_by_any_signed_in_user_but_not_writable()
    {
        await CreateProjectAsync(ClientFor("alice"), "shared", "internal");
        var bob = ClientFor("bob");
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/v1/projects/shared")).StatusCode);
        var patch = await bob.PatchAsJsonAsync("/api/v1/projects/shared", new { name = "Mine" });
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal("insufficient_role", await ProblemCodeAsync(patch));
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync("/api/v1/projects/shared")).StatusCode);
    }

    [Fact]
    public async Task Admin_can_patch_name_description_and_visibility_and_absent_fields_are_untouched()
    {
        var alice = ClientFor("alice");
        await alice.PostAsJsonAsync("/api/v1/projects", new { slug = "p", name = "Old", description = "keep me" });
        var r = await alice.PatchAsJsonAsync("/api/v1/projects/p", new { name = "New", visibility = "internal" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var p = await ReadAsync<ProjectDto>(r);
        Assert.Equal("p", p.Slug);
        Assert.Equal("New", p.Name);
        Assert.Equal("keep me", p.Description);
        Assert.Equal("internal", p.Visibility);
    }

    [Fact]
    public async Task Patch_rejects_an_invalid_visibility()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PatchAsJsonAsync("/api/v1/projects/p", new { visibility = "everyone" })).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_project_and_its_memberships_so_the_slug_can_be_reused()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p")).StatusCode);

        await CreateProjectAsync(ClientFor("bob"), "p");
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p")).StatusCode);
    }
}
