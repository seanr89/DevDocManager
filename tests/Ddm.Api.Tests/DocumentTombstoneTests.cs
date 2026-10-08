using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class DocumentTombstoneTests(PostgresFixture pg) : ApiTestBase(pg)
{
    /// <summary>a.md at v2, then deleted.</summary>
    private async Task<HttpClient> DeletedAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutDocAsync(alice, "p", "a.md", "# one");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/docs/a.md")).StatusCode);
        return alice;
    }

    [Fact]
    public async Task Tombstones_are_hidden_from_the_list_but_shown_with_deleted_true()
    {
        var alice = await DeletedAsync();
        await PutDocAsync(alice, "p", "b.md", "# b");
        var live = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"));
        Assert.Equal(["b.md"], live.Items.Select(d => d.Path));
        var deleted = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs?deleted=true"));
        Assert.Equal(["a.md"], deleted.Items.Select(d => d.Path));
        Assert.Equal(2, deleted.Items[0].Version);
    }

    [Fact]
    public async Task History_and_old_versions_stay_readable_after_delete()
    {
        var alice = await DeletedAsync();
        var page = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions"));
        Assert.Equal([2, 1], page.Items.Select(v => v.Number));
        var v1 = await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions/1");
        Assert.Equal("# one", await v1.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Restoring_a_tombstoned_document_revives_it_as_the_next_version()
    {
        var alice = await DeletedAsync();
        var r = await alice.PostAsync("/api/v1/projects/p/docs/a.md/versions/1/restore", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("\"v3\"", r.Headers.ETag!.Tag);
        Assert.Equal("# one", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task If_match_against_a_tombstone_fails_and_if_none_match_star_revives()
    {
        var alice = await DeletedAsync();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutDocAsync(alice, "p", "a.md", "# x", ifMatch: "\"v2\"")).StatusCode);
        var r = await PutDocAsync(alice, "p", "a.md", "# x", ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v3\"", r.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Reviving_with_the_same_content_as_the_last_version_still_revives()
    {
        var alice = await DeletedAsync();
        var r = await PutDocAsync(alice, "p", "a.md", "# two");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_tombstone_again_is_404()
    {
        var alice = await DeletedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync("/api/v1/projects/p/docs/a.md")).StatusCode);
    }

    [Fact]
    public async Task Post_create_over_a_tombstone_succeeds()
    {
        var alice = await DeletedAsync();
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "a.md", content = "# again" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v3\"", r.Headers.ETag!.Tag);
    }
}
