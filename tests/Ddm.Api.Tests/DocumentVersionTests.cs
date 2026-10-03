using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class DocumentVersionTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<HttpClient> ThreeVersionsAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutDocAsync(alice, "p", "a.md", "# one", message: "first");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"", message: "second");
        await PutDocAsync(alice, "p", "a.md", "# three", ifMatch: "\"v2\"");
        return alice;
    }

    private static Task<HttpResponseMessage> Restore(HttpClient c, int n, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/p/docs/a.md/versions/{n}/restore");
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return c.SendAsync(request);
    }

    [Fact]
    public async Task History_lists_versions_newest_first_with_author_and_message()
    {
        var alice = await ThreeVersionsAsync();
        var page = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions"));
        Assert.Equal([3, 2, 1], page.Items.Select(v => v.Number));
        Assert.Equal("second", page.Items[1].Message);
        Assert.Equal("first", page.Items[2].Message);
        Assert.Null(page.Items[0].Message);
        Assert.All(page.Items, v => Assert.Equal("user:alice", v.Author));
    }

    [Fact]
    public async Task History_pages_with_a_cursor()
    {
        var alice = await ThreeVersionsAsync();
        var first = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions?limit=2"));
        Assert.Equal([3, 2], first.Items.Select(v => v.Number));
        var second = await ReadAsync<Page<VersionDto>>(
            await alice.GetAsync($"/api/v1/projects/p/docs/a.md/versions?limit=2&cursor={Uri.EscapeDataString(first.Next!)}"));
        Assert.Equal([1], second.Items.Select(v => v.Number));
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task An_old_version_can_be_read_with_its_own_etag()
    {
        var alice = await ThreeVersionsAsync();
        var r = await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions/1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("# one", await r.Content.ReadAsStringAsync());
        Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
    }

    [Theory] [InlineData("99")] [InlineData("0")] [InlineData("99999999999")]
    public async Task Unknown_versions_are_404(string n)
    {
        var alice = await ThreeVersionsAsync();
        var r = await alice.GetAsync($"/api/v1/projects/p/docs/a.md/versions/{n}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("version_not_found", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task History_of_a_missing_document_is_404()
    {
        var alice = await ThreeVersionsAsync();
        var r = await alice.GetAsync("/api/v1/projects/p/docs/nope.md/versions");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("document_not_found", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Restore_creates_a_new_version_and_keeps_history_intact()
    {
        var alice = await ThreeVersionsAsync();
        var r = await Restore(alice, 1);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("\"v4\"", r.Headers.ETag!.Tag);

        Assert.Equal("# one", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
        Assert.Equal("# two", await alice.GetStringAsync("/api/v1/projects/p/docs/a.md/versions/2"));
        Assert.Equal("# three", await alice.GetStringAsync("/api/v1/projects/p/docs/a.md/versions/3"));
        var history = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions"));
        Assert.Equal([4, 3, 2, 1], history.Items.Select(v => v.Number));
        Assert.Equal("Restore version 1", history.Items[0].Message);
    }

    [Fact]
    public async Task Restoring_content_that_is_already_current_is_a_no_op()
    {
        var alice = await ThreeVersionsAsync();
        await Restore(alice, 1);
        var again = await Restore(alice, 1);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("\"v4\"", again.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Restore_honours_a_stale_if_match()
    {
        var alice = await ThreeVersionsAsync();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Restore(alice, 1, ifMatch: "\"v1\"")).StatusCode);
        Assert.Equal("# three", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Restore_of_an_unknown_version_is_404()
    {
        var alice = await ThreeVersionsAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(alice, 99)).StatusCode);
    }

    [Fact]
    public async Task Readers_can_read_history_but_not_restore()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p", "internal");
        await PutDocAsync(alice, "p", "a.md", "# one");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        var bob = ClientFor("bob");
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/v1/projects/p/docs/a.md/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Restore(bob, 1)).StatusCode);
    }

    [Fact]
    public async Task A_non_numeric_version_is_a_path_error()
    {
        var alice = await ThreeVersionsAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions/x")).StatusCode);
    }
}
