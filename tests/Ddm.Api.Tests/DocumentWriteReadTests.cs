using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class DocumentWriteReadTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private const string Page = "---\ntitle: Setup\ntags: [onboarding, guide]\n---\n# Setup\n\nHello.\n";

    private async Task<HttpClient> ProjectWithAliceAsync(string slug = "p", string visibility = "private")
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, slug, visibility);
        return alice;
    }

    private async Task<T> WithDbAsync<T>(Func<DdmDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<DdmDbContext>());
    }

    [Fact]
    public async Task Put_creates_a_document_at_version_1()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", "guides/setup.md", Page);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
        var dto = await ReadAsync<DocumentDto>(r);
        Assert.Equal("guides/setup.md", dto.Path);
        Assert.Equal("Setup", dto.Title);
        Assert.Equal(1, dto.Version);
        Assert.Equal("onboarding", dto.FrontMatter.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public async Task Get_returns_the_original_markdown_by_default_and_by_accept()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "setup.md", Page);
        foreach (var accept in new string?[] { null, "text/markdown", "*/*" })
        {
            var r = await GetDocAsync(alice, "p", "setup.md", accept);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.StartsWith("text/markdown", r.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Page, await r.Content.ReadAsStringAsync());
            Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
        }
    }

    [Fact]
    public async Task Get_with_accept_html_returns_sanitized_html_without_the_front_matter()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "x.md", "---\ntitle: T\n---\n# Hi\n\n<script>alert(1)</script>\n");
        var r = await GetDocAsync(alice, "p", "x.md", "text/html");
        Assert.StartsWith("text/html", r.Content.Headers.ContentType!.MediaType);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("<h1", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("title: T", html);
    }

    [Fact]
    public async Task Get_with_accept_json_returns_metadata()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "setup.md", Page);
        var dto = await ReadAsync<DocumentDto>(await GetDocAsync(alice, "p", "setup.md", "application/json"));
        Assert.Equal("Setup", dto.Title);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public async Task Get_with_an_unsupported_accept_is_406()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "setup.md", Page);
        var r = await GetDocAsync(alice, "p", "setup.md", "application/xml");
        Assert.Equal(HttpStatusCode.NotAcceptable, r.StatusCode);
        Assert.Equal("not_acceptable", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Get_of_a_missing_document_is_404()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await GetDocAsync(alice, "p", "nope.md");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("document_not_found", await ProblemCodeAsync(r));
    }

    [Theory] [InlineData("x.txt")] [InlineData("a//b.md")] [InlineData(".hidden.md")]
    public async Task Invalid_paths_are_a_400_and_nothing_is_stored(string path)
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", path, "# x");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_path", await ProblemCodeAsync(r));
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Updating_an_existing_document_requires_if_match()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# one");
        var r = await PutDocAsync(alice, "p", "a.md", "# two");
        Assert.Equal((HttpStatusCode)428, r.StatusCode);
        Assert.Equal("precondition_required", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Update_with_the_current_etag_creates_the_next_version()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# one");
        var r = await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"", message: "second draft");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("\"v2\"", r.Headers.ETag!.Tag);
        Assert.Equal("# two", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
        var v2 = await WithDbAsync(db => db.Versions.SingleAsync(v => v.Number == 2));
        Assert.Equal("second draft", v2.Message);
        Assert.Equal("user:alice", v2.Author);
    }

    [Fact]
    public async Task A_stale_etag_returns_412_and_leaves_the_content_alone()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# one");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        var r = await PutDocAsync(alice, "p", "a.md", "# stale overwrite", ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, r.StatusCode);
        Assert.Equal("precondition_failed", await ProblemCodeAsync(r));
        Assert.Equal("# two", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task If_match_on_a_document_that_does_not_exist_is_412()
    {
        var alice = await ProjectWithAliceAsync();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutDocAsync(alice, "p", "new.md", "# x", ifMatch: "\"v1\"")).StatusCode);
    }

    [Fact]
    public async Task If_none_match_star_means_create_only()
    {
        var alice = await ProjectWithAliceAsync();
        Assert.Equal(HttpStatusCode.Created, (await PutDocAsync(alice, "p", "a.md", "# x", ifNoneMatch: "*")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutDocAsync(alice, "p", "a.md", "# y", ifNoneMatch: "*")).StatusCode);
    }

    [Fact]
    public async Task A_malformed_if_match_is_a_400()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# x");
        var r = await PutDocAsync(alice, "p", "a.md", "# y", ifMatch: "v1");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_etag", await ProblemCodeAsync(r));
    }

    // Review Focus 2
    [Fact]
    public async Task Republishing_identical_content_creates_no_new_version()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", Page);
        var again = await PutDocAsync(alice, "p", "a.md", Page, ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("\"v1\"", again.Headers.ETag!.Tag);
        Assert.Equal(1, await WithDbAsync(db => db.Versions.CountAsync()));
        Assert.Equal(1, await WithDbAsync(db => db.AuditEntries.CountAsync(e => e.Action.StartsWith("doc."))));
    }

    // Review Focus 1
    [Fact]
    public async Task Concurrent_writers_with_the_same_etag_produce_one_winner_and_no_500s()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# v1");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => PutDocAsync(alice, "p", "a.md", $"# change {i}", ifMatch: "\"v1\"")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
        Assert.Equal(2, await WithDbAsync(db => db.Versions.CountAsync()));
    }

    [Fact]
    public async Task Concurrent_creates_of_the_same_path_produce_one_winner_and_no_500s()
    {
        var alice = await ProjectWithAliceAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => PutDocAsync(alice, "p", "same.md", $"# create {i}")));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        // A loser that raced the insert gets 412; one that arrived after the winner committed is updating
        // an existing document without If-Match, so it gets 428. Either way nothing is overwritten.
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.PreconditionFailed, (HttpStatusCode)428 }));
        Assert.Equal(1, await WithDbAsync(db => db.Documents.CountAsync()));
    }

    [Fact]
    public async Task A_storage_outage_fails_the_write_and_stores_nothing()
    {
        var alice = await ProjectWithAliceAsync();
        Factory.Blobs.FailNextPut = true;
        var r = await PutDocAsync(alice, "p", "a.md", "# x");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal("internal_error", await ProblemCodeAsync(r));
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.Documents.CountAsync()));
        Assert.Equal(0, await WithDbAsync(db => db.Versions.CountAsync()));
    }

    [Fact]
    public async Task Malformed_front_matter_is_a_400_and_nothing_is_stored()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", "a.md", "---\ntitle: [oops\n---\n# x");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_front_matter", await ProblemCodeAsync(r));
        Assert.Equal(0, Factory.Blobs.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
    }

    [Fact]
    public async Task An_empty_body_creates_an_empty_document_titled_by_its_file_name()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", "empty.md", "");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("empty", (await ReadAsync<DocumentDto>(r)).Title);
    }

    [Fact]
    public async Task A_body_over_one_mebibyte_is_413()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", "big.md", new string('a', DocumentService.MaxBytes + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal("payload_too_large", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task A_body_that_is_not_utf8_is_a_400()
    {
        var alice = await ProjectWithAliceAsync();
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/projects/p/docs/bad.md")
        {
            Content = new ByteArrayContent([0xFF, 0xFE, 0xFD]),
        };
        request.Content.Headers.ContentType = new("text/markdown");
        var r = await alice.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_encoding", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task A_non_markdown_content_type_is_415()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await alice.PutAsync("/api/v1/projects/p/docs/a.md", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
    }

    [Fact]
    public async Task Readers_cannot_write_and_strangers_cannot_even_see_the_project()
    {
        var alice = await ProjectWithAliceAsync("shared", "internal");
        await CreateProjectAsync(alice, "secret", "private");
        var bob = ClientFor("bob");

        var forbidden = await PutDocAsync(bob, "shared", "a.md", "# x");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("insufficient_role", await ProblemCodeAsync(forbidden));

        Assert.Equal(HttpStatusCode.NotFound, (await PutDocAsync(bob, "secret", "a.md", "# x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(bob, "secret", "a.md")).StatusCode);
        // and an invalid path must not turn the 404 into a 400 (that would confirm the project exists)
        Assert.Equal(HttpStatusCode.NotFound, (await PutDocAsync(bob, "secret", "x.txt", "# x")).StatusCode);
    }

    [Fact]
    public async Task Readers_can_read_internal_documents()
    {
        var alice = await ProjectWithAliceAsync("shared", "internal");
        await PutDocAsync(alice, "shared", "a.md", "# x");
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(ClientFor("bob"), "shared", "a.md")).StatusCode);
    }

    [Fact]
    public async Task The_same_path_in_two_projects_is_independent()
    {
        var alice = await ProjectWithAliceAsync("one");
        await CreateProjectAsync(alice, "two");
        await PutDocAsync(alice, "one", "a.md", "# one");
        await PutDocAsync(alice, "two", "a.md", "# two");
        Assert.Equal("# one", await (await GetDocAsync(alice, "one", "a.md")).Content.ReadAsStringAsync());
        Assert.Equal("# two", await (await GetDocAsync(alice, "two", "a.md")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Write_tokens_can_publish_and_read_tokens_cannot()
    {
        var alice = await ProjectWithAliceAsync();
        var writer = TokenClient(await CreateTokenAsync(alice, "p", "write"));
        var reader = TokenClient(await CreateTokenAsync(alice, "p", "read", "ro"));

        Assert.Equal(HttpStatusCode.Created, (await PutDocAsync(writer, "p", "ci.md", "# from ci")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutDocAsync(reader, "p", "ci2.md", "# nope")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(reader, "p", "ci.md")).StatusCode);
        var version = await WithDbAsync(db => db.Versions.SingleAsync());
        Assert.StartsWith("token:", version.Author);
    }

    [Fact]
    public async Task Post_creates_a_document_and_a_duplicate_is_409()
    {
        var alice = await ProjectWithAliceAsync();
        var created = await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "guides/new.md", content = "# New", message = "init" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/v1/projects/p/docs/guides/new.md", created.Headers.Location!.ToString());

        var dup = await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "guides/new.md", content = "# Again" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("document_exists", await ProblemCodeAsync(dup));
    }

    [Fact]
    public async Task Post_validates_path_and_content()
    {
        var alice = await ProjectWithAliceAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "x.txt", content = "# x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "x.md" })).StatusCode);
    }

    [Fact]
    public async Task Writes_are_audited()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# one");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        var actions = await WithDbAsync(db => db.AuditEntries.Where(e => e.Action.StartsWith("doc.")).OrderBy(e => e.Id).Select(e => e.Action).ToListAsync());
        Assert.Equal(["doc.create", "doc.update"], actions);
    }
}
