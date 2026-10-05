using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Tags;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class DocumentTagTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<HttpClient> AliceAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        return alice;
    }

    private static Task<HttpResponseMessage> PutTagsAsync(HttpClient c, string url, params string[] tags) =>
        c.PutAsJsonAsync(url, new { tags });

    private static async Task<IReadOnlyList<string>> DocTagsAsync(HttpClient c, string path) =>
        (await ReadAsync<DocumentDto>(await GetDocAsync(c, "p", path, "application/json"))).Tags;

    [Fact]
    public async Task Front_matter_tags_are_normalised_and_returned()
    {
        var alice = await AliceAsync();
        var r = await PutDocAsync(alice, "p", "a.md", "---\ntags: [Guide, Getting Started]\n---\n# A");
        Assert.Equal(["getting-started", "guide"], (await ReadAsync<DocumentDto>(r)).Tags);
        Assert.Equal(["getting-started", "guide"], await DocTagsAsync(alice, "a.md"));
    }

    [Fact]
    public async Task Invalid_front_matter_tags_fail_the_write()
    {
        var alice = await AliceAsync();
        var r = await PutDocAsync(alice, "p", "a.md", "---\ntags: [\"bad/tag\"]\n---\n# A");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_tag", await ProblemCodeAsync(r));
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
    }

    [Fact]
    public async Task Api_tags_are_rejected_when_front_matter_owns_them()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [guide]\n---\n# A");
        var r = await PutTagsAsync(alice, "/api/v1/projects/p/docs/a.md/tags", "other");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("tags_managed_by_front_matter", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Api_tags_replace_a_documents_tags_without_a_new_version()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# A");
        var r = await PutTagsAsync(alice, "/api/v1/projects/p/docs/a.md/tags", "b", "a");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(["a", "b"], (await ReadAsync<TagsDto>(r)).Tags);
        await PutTagsAsync(alice, "/api/v1/projects/p/docs/a.md/tags", "c");
        var doc = await GetDocAsync(alice, "p", "a.md", "application/json");
        Assert.Equal("\"v1\"", doc.Headers.ETag!.Tag);
        Assert.Equal(["c"], (await ReadAsync<DocumentDto>(doc)).Tags);
    }

    // Review Focus 4
    [Fact]
    public async Task Removing_the_tags_key_keeps_tags_and_an_empty_list_clears_them()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [guide]\n---\n# A");
        await PutDocAsync(alice, "p", "a.md", "# A without front matter", ifMatch: "\"v1\"");
        Assert.Equal(["guide"], await DocTagsAsync(alice, "a.md"));
        await PutDocAsync(alice, "p", "a.md", "---\ntags: []\n---\n# A", ifMatch: "\"v2\"");
        Assert.Empty(await DocTagsAsync(alice, "a.md"));
    }

    // Review Focus 4
    [Fact]
    public async Task A_revived_document_keeps_its_tags()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# A");
        await PutTagsAsync(alice, "/api/v1/projects/p/docs/a.md/tags", "keep");
        await alice.DeleteAsync("/api/v1/projects/p/docs/a.md");
        await PutDocAsync(alice, "p", "a.md", "# back");
        Assert.Equal(["keep"], await DocTagsAsync(alice, "a.md"));
    }

    [Fact]
    public async Task Tags_on_a_missing_or_tombstoned_document_are_404()
    {
        var alice = await AliceAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await PutTagsAsync(alice, "/api/v1/projects/p/docs/nope.md/tags", "x")).StatusCode);
        await PutDocAsync(alice, "p", "gone.md", "# g");
        await alice.DeleteAsync("/api/v1/projects/p/docs/gone.md");
        Assert.Equal(HttpStatusCode.NotFound, (await PutTagsAsync(alice, "/api/v1/projects/p/docs/gone.md/tags", "x")).StatusCode);
    }

    [Fact]
    public async Task A_tags_body_is_required()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# A");
        var r = await alice.PutAsJsonAsync("/api/v1/projects/p/docs/a.md/tags", new { });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("validation_failed", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task List_filters_by_every_tag_given_and_includes_tags()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [x, y]\n---\n# A");
        await PutDocAsync(alice, "p", "b.md", "---\ntags: [x]\n---\n# B");
        await PutDocAsync(alice, "p", "c.md", "# C");
        var both = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs?tag=x&tag=Y"));
        Assert.Equal(["a.md"], both.Items.Select(d => d.Path));
        Assert.Equal(["x", "y"], both.Items[0].Tags);
        var x = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs?tag=x"));
        Assert.Equal(["a.md", "b.md"], x.Items.Select(d => d.Path));
    }

    [Fact]
    public async Task Vocabulary_counts_live_items_and_admins_can_delete_a_tag()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [x, y]\n---\n# A");
        await PutDocAsync(alice, "p", "b.md", "---\ntags: [x]\n---\n# B");
        await alice.DeleteAsync("/api/v1/projects/p/docs/b.md");
        var vocab = await ReadAsync<Page<TagCountDto>>(await alice.GetAsync("/api/v1/projects/p/tags"));
        Assert.Equal([("x", 1), ("y", 1)], vocab.Items.Select(t => (t.Name, t.Count)));

        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/tags/x")).StatusCode);
        Assert.Equal(["y"], await DocTagsAsync(alice, "a.md"));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync("/api/v1/projects/p/tags/x")).StatusCode);
    }

    [Fact]
    public async Task Editors_cannot_delete_tags_and_readers_cannot_set_them()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [x]\n---\n# A");
        await PutDocAsync(alice, "p", "b.md", "# B");
        await AddMemberAsync(alice, "p", "ed", "editor");
        await AddMemberAsync(alice, "p", "rd", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientFor("ed").DeleteAsync("/api/v1/projects/p/tags/x")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutTagsAsync(ClientFor("rd"), "/api/v1/projects/p/docs/b.md/tags", "y")).StatusCode);
    }
}
