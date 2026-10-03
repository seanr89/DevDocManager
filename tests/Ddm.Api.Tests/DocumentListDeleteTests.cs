using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class DocumentListDeleteTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<HttpClient> SeedAsync(params string[] paths)
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        foreach (var path in paths) await PutDocAsync(alice, "p", path, $"# {path}");
        return alice;
    }

    [Fact]
    public async Task List_returns_paths_titles_and_versions_in_path_order()
    {
        var alice = await SeedAsync("b.md", "a/z.md", "a/y.md");
        var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"));
        Assert.Equal(["a/y.md", "a/z.md", "b.md"], page.Items.Select(d => d.Path));
        Assert.All(page.Items, d => Assert.Equal(1, d.Version));
        Assert.Equal("# a/y.md".TrimStart('#', ' '), page.Items[0].Title);
    }

    [Fact]
    public async Task List_of_an_empty_project_is_empty()
    {
        var alice = await SeedAsync();
        var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"));
        Assert.Empty(page.Items);
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task List_pages_through_every_document_once()
    {
        var alice = await SeedAsync("d1.md", "d2.md", "d3.md", "d4.md", "d5.md");
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var url = "/api/v1/projects/p/docs?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync(url));
            seen.AddRange(page.Items.Select(d => d.Path));
            cursor = page.Next;
        } while (cursor is not null);
        Assert.Equal(["d1.md", "d2.md", "d3.md", "d4.md", "d5.md"], seen);
    }

    [Fact]
    public async Task Prefix_filters_by_folder_and_treats_underscore_literally()
    {
        var alice = await SeedAsync("a_b/x.md", "axb/y.md", "a_b/sub/z.md");
        var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs?prefix=a_b/"));
        Assert.Equal(["a_b/sub/z.md", "a_b/x.md"], page.Items.Select(d => d.Path));
    }

    [Fact]
    public async Task List_only_shows_this_projects_documents()
    {
        var alice = await SeedAsync("mine.md");
        await CreateProjectAsync(alice, "other");
        await PutDocAsync(alice, "other", "theirs.md", "# t");
        var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"));
        Assert.Equal(["mine.md"], page.Items.Select(d => d.Path));
    }

    [Fact]
    public async Task List_hides_a_private_project_from_strangers()
    {
        await SeedAsync("a.md");
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("bob").GetAsync("/api/v1/projects/p/docs")).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_document_and_its_versions()
    {
        var alice = await SeedAsync("a.md");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/docs/a.md")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
        Assert.Equal(0, await db.Versions.CountAsync());
        Assert.Contains(await db.AuditEntries.Select(e => e.Action).ToListAsync(), a => a == "doc.delete");
    }

    [Fact]
    public async Task A_deleted_path_can_be_created_again_from_version_1()
    {
        var alice = await SeedAsync("a.md");
        await alice.DeleteAsync("/api/v1/projects/p/docs/a.md");
        var r = await PutDocAsync(alice, "p", "a.md", "# reborn");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Delete_of_a_missing_document_is_404()
    {
        var alice = await SeedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync("/api/v1/projects/p/docs/nope.md")).StatusCode);
    }

    [Fact]
    public async Task Delete_honours_a_stale_if_match()
    {
        var alice = await SeedAsync("a.md");
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/projects/p/docs/a.md");
        request.Headers.TryAddWithoutValidation("If-Match", "\"v9\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await alice.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
    }

    [Fact]
    public async Task Readers_cannot_delete()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "shared", "internal");
        await PutDocAsync(alice, "shared", "a.md", "# x");
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientFor("bob").DeleteAsync("/api/v1/projects/shared/docs/a.md")).StatusCode);
    }
}
