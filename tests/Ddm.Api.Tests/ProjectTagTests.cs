using Ddm.Api.Common;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class ProjectTagTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private static Task<HttpResponseMessage> SetTagsAsync(HttpClient c, string slug, params string[] tags) =>
        c.PatchAsJsonAsync($"/api/v1/projects/{slug}", new { tags });

    [Fact]
    public async Task Admins_set_project_tags_with_patch()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await SetTagsAsync(alice, "p", "Platform", "api");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(["api", "platform"], (await ReadAsync<ProjectDto>(r)).Tags);
        Assert.Equal(["api", "platform"], (await ReadAsync<ProjectDto>(await alice.GetAsync("/api/v1/projects/p"))).Tags);
    }

    [Fact]
    public async Task A_patch_without_tags_leaves_them_alone()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await SetTagsAsync(alice, "p", "api");
        var r = await alice.PatchAsJsonAsync("/api/v1/projects/p", new { name = "Renamed" });
        Assert.Equal(["api"], (await ReadAsync<ProjectDto>(r)).Tags);
    }

    [Fact]
    public async Task A_new_project_has_no_tags()
    {
        var alice = ClientFor("alice");
        Assert.Empty((await CreateProjectAsync(alice, "p")).Tags);
    }

    [Fact]
    public async Task Editors_cannot_set_project_tags()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "ed", "editor");
        Assert.Equal(HttpStatusCode.Forbidden, (await SetTagsAsync(ClientFor("ed"), "p", "x")).StatusCode);
    }

    [Fact]
    public async Task Tag_filter_never_reveals_projects_the_caller_cannot_see()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "secret");
        await CreateProjectAsync(alice, "open", "internal");
        await SetTagsAsync(alice, "secret", "shared");
        await SetTagsAsync(alice, "open", "shared");

        var bob = await ReadAsync<Page<ProjectDto>>(await ClientFor("bob").GetAsync("/api/v1/projects?tag=shared"));
        Assert.Equal(["open"], bob.Items.Select(p => p.Slug));
        var mine = await ReadAsync<Page<ProjectDto>>(await alice.GetAsync("/api/v1/projects?tag=shared"));
        Assert.Equal(["open", "secret"], mine.Items.Select(p => p.Slug));
        Assert.Empty((await ReadAsync<Page<ProjectDto>>(await alice.GetAsync("/api/v1/projects?tag=nothing"))).Items);
    }

    [Fact]
    public async Task A_read_token_cannot_see_or_set_another_projects_tags()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "a");
        await CreateProjectAsync(alice, "b");
        await PutDocAsync(alice, "b", "x.md", "# x");
        var token = TokenClient(await CreateTokenAsync(alice, "a", "read"));
        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync("/api/v1/projects/b/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await token.PutAsJsonAsync("/api/v1/projects/b/docs/x.md/tags", new { tags = new[] { "t" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetTagsAsync(token, "a", "t")).StatusCode);
    }
}
