using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Publishing;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class PublishTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private const string Spec = "openapi: 3.0.3\ninfo: {title: Pay, version: '1'}\npaths:\n  /pay:\n    post:\n      responses:\n        '200': {description: ok}\n";
    private static byte[] T(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] Folder(params (string Name, byte[] Content)[] files) => Archives.TarGz(files);

    private async Task<HttpClient> AliceAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        return alice;
    }

    private static async Task<PublishResult> OkAsync(Task<HttpResponseMessage> call)
    {
        var r = await call;
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return await ReadAsync<PublishResult>(r);
    }

    [Fact]
    public async Task A_folder_of_markdown_images_and_a_spec_publishes_in_one_call()
    {
        var alice = await AliceAsync();
        var ci = TokenClient(await CreateTokenAsync(alice, "p", "write"));
        var result = await OkAsync(PublishAsync(ci, "p", Folder(
            ("index.md", T("---\ntags: [home]\n---\n# Home\n\n![logo](images/logo.png)")),
            ("images/logo.png", PngBytes),
            ("apis/payments.yaml", T(Spec))), "?message=abc123"));

        Assert.Equal(3, result.Created.Count);
        Assert.Contains(result.Created, i => i is { Type: "document", Key: "index.md", Version: 1 });
        Assert.Contains(result.Created, i => i is { Type: "asset", Key: "images/logo.png", Version: null });
        Assert.Contains(result.Created, i => i is { Type: "spec", Key: "payments", Version: 1 });
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/projects/p/specs/payments")).StatusCode);
        var history = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/index.md/versions"));
        Assert.Equal("abc123", history.Items[0].Message);
        Assert.Equal(["home"], (await ReadAsync<DocumentDto>(await GetDocAsync(alice, "p", "index.md", "application/json"))).Tags);
        Assert.Contains("/content/", await (await GetDocAsync(alice, "p", "index.md", "text/html")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Republishing_the_same_folder_changes_nothing()
    {
        var alice = await AliceAsync();
        var folder = Folder(("a.md", T("# a")), ("b.png", PngBytes));
        await OkAsync(PublishAsync(alice, "p", folder));
        var again = await OkAsync(PublishAsync(alice, "p", folder));
        Assert.Empty(again.Created);
        Assert.Empty(again.Updated);
        Assert.Empty(again.Deleted);
        Assert.Equal(2, again.Unchanged);
    }

    [Fact]
    public async Task Mirror_deletes_what_the_folder_no_longer_has_and_it_can_be_restored()
    {
        var alice = await AliceAsync();
        await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), ("b.md", T("# b")), ("c.md", T("# c")))));
        var result = await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), ("b.md", T("# b2")))));
        Assert.Equal(["b.md"], result.Updated.Select(i => i.Key));
        Assert.Equal(["c.md"], result.Deleted.Select(i => i.Key));
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "c.md")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/v1/projects/p/docs/c.md/versions/1/restore", null)).StatusCode);
    }

    [Fact]
    public async Task A_prefix_publishes_into_a_subtree_and_leaves_the_rest_alone()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "handwritten.md", "# keep");
        await PutDocAsync(alice, "p", "guides/a.md", "# a");
        await PutDocAsync(alice, "p", "guides/old.md", "# old");
        var result = await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), ("setup.md", T("# s"))), "?prefix=guides/"));
        Assert.Equal(["guides/setup.md"], result.Created.Select(i => i.Key));
        Assert.Equal(["guides/old.md"], result.Deleted.Select(i => i.Key));
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", "handwritten.md")).StatusCode);
    }

    // Spec review focus 1: mirror safety
    [Fact]
    public async Task Empty_or_half_built_archives_trip_the_mass_delete_guard()
    {
        var alice = await AliceAsync();
        (string, byte[])[] Docs(int n) => Enumerable.Range(0, n).Select(i => ($"d{i}.md", T($"# {i}"))).ToArray();
        await OkAsync(PublishAsync(alice, "p", Folder(Docs(12))));

        var empty = await PublishAsync(alice, "p", Archives.TarGzEntries());
        Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
        Assert.Equal("publish_mass_delete", await ProblemCodeAsync(empty));
        Assert.Equal("publish_mass_delete", await ProblemCodeAsync(await PublishAsync(alice, "p", Folder(Docs(5)))));
        Assert.Equal(12, (await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"))).Items.Count);

        var forced = await OkAsync(PublishAsync(alice, "p", Archives.TarGzEntries(), "?allowMassDelete=true"));
        Assert.Equal(12, forced.Deleted.Count);
    }

    // Spec review focus 1: a folder meant for guides/ published with the wrong (empty) prefix
    [Fact]
    public async Task A_wrong_prefix_that_would_wipe_other_folders_trips_the_mass_delete_guard()
    {
        var alice = await AliceAsync();
        var existing = Enumerable.Range(0, 8).Select(i => ($"guides/g{i}.md", T($"# g{i}")))
            .Concat(Enumerable.Range(0, 4).Select(i => ($"reference/r{i}.md", T($"# r{i}")))).ToArray();
        await OkAsync(PublishAsync(alice, "p", Folder(existing)));

        // The folder's own files, relative to guides/; the caller forgot ?prefix=guides/, so the scope is the whole project.
        var wrong = await PublishAsync(alice, "p", Folder(("g0.md", T("# g0")), ("g1.md", T("# g1"))));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("publish_mass_delete", await ProblemCodeAsync(wrong));
        var dry = await PublishAsync(alice, "p", Folder(("g0.md", T("# g0")), ("g1.md", T("# g1"))), "?dryRun=true");
        Assert.Equal("publish_mass_delete", await ProblemCodeAsync(dry));
        var docs = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs?limit=100"));
        Assert.Equal(12, docs.Items.Count);
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", "reference/r0.md")).StatusCode);

        // With the right prefix the same folder is a normal mirror of guides/ and reference/ is untouched.
        var right = await OkAsync(PublishAsync(alice, "p", Folder(("g0.md", T("# g0")), ("g1.md", T("# g1")), ("g2.md", T("# g2")),
            ("g3.md", T("# g3")), ("g4.md", T("# g4")), ("g5.md", T("# g5")), ("g6.md", T("# g6"))), "?prefix=guides/"));
        Assert.Equal(["guides/g7.md"], right.Deleted.Select(i => i.Key));
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", "reference/r0.md")).StatusCode);
    }

    // Spec review focus 1: after an allowed mass delete every item can still be brought back
    [Fact]
    public async Task After_an_allowed_mass_delete_every_item_can_be_brought_back()
    {
        var alice = await AliceAsync();
        var docFiles = Enumerable.Range(0, 5).Select(i => ($"d{i}.md", T($"# d{i}")));
        await OkAsync(PublishAsync(alice, "p", Folder([.. docFiles, ("img/logo.png", PngBytes), ("apis/pay.yaml", T(Spec))])));
        var forced = await OkAsync(PublishAsync(alice, "p", Archives.TarGzEntries(), "?allowMassDelete=true"));
        Assert.Equal(7, forced.Deleted.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p/assets/img/logo.png")).StatusCode);

        // Assets have no restore endpoint: for them "restorable" means publishing the file again revives the tombstoned row.
        // Done first, while nothing else is live, so the mass-delete guard has nothing to object to.
        var revived = await OkAsync(PublishAsync(alice, "p", Folder(("img/logo.png", PngBytes))));
        Assert.Equal(["img/logo.png"], revived.Created.Select(i => i.Key));
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/projects/p/assets/img/logo.png")).StatusCode);

        // Documents and specs come back from their history.
        foreach (var d in forced.Deleted.Where(i => i.Type == "document"))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", d.Key)).StatusCode);
            var history = await ReadAsync<Page<VersionDto>>(await alice.GetAsync($"/api/v1/projects/p/docs/{d.Key}/versions"));
            Assert.Single(history.Items);
            Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/projects/p/docs/{d.Key}/versions/1/restore", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", d.Key)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p/specs/pay")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/projects/p/specs/pay/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/v1/projects/p/specs/pay/versions/1/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/projects/p/specs/pay")).StatusCode);
    }

    // Spec review focus 2: atomicity
    [Fact]
    public async Task One_bad_file_fails_the_publish_and_writes_nothing()
    {
        var alice = await AliceAsync();
        var r = await PublishAsync(alice, "p", Folder(
            ("good.md", T("# good")),
            ("bad.svg", PngBytes),
            ("api.yaml", T("openapi: 3.0.3\ninfo: {version: '1'}\npaths: {}\n")),
            ("tool.exe", [1])));
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("publish_invalid", body.GetProperty("code").GetString());
        var codes = body.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()).ToList();
        Assert.Contains("asset_type_mismatch", codes);
        Assert.Contains("invalid_spec", codes);
        Assert.Contains("unsupported_file", codes);
        Assert.Equal(0, Factory.Blobs.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "good.md")).StatusCode);
    }

    [Fact]
    public async Task Two_spec_files_with_the_same_stem_are_refused()
    {
        var alice = await AliceAsync();
        var r = await PublishAsync(alice, "p", Folder(("v1/api.yaml", T(Spec)), ("v2/api.yaml", T(Spec))));
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("duplicate_spec_name", body.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Dry_run_reports_the_plan_and_writes_nothing()
    {
        var alice = await AliceAsync();
        var plan = await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a"))), "?dryRun=true"));
        Assert.True(plan.DryRun);
        Assert.Equal(["a.md"], plan.Created.Select(i => i.Key));
        Assert.Null(plan.Created[0].Version);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Zip_archives_publish_too()
    {
        var alice = await AliceAsync();
        var result = await OkAsync(PublishAsync(alice, "p", Archives.Zip(("a.md", T("# a"))), mediaType: "application/zip"));
        Assert.Equal(["a.md"], result.Created.Select(i => i.Key));
    }

    [Fact]
    public async Task Hidden_files_are_ignored_and_reported()
    {
        var alice = await AliceAsync();
        var result = await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), (".github/workflows/docs.yml", T("on: push")))));
        Assert.Equal([".github/workflows/docs.yml"], result.Ignored);
    }

    [Fact]
    public async Task Hostile_archives_are_rejected_before_anything_is_written()
    {
        var alice = await AliceAsync();
        var r = await PublishAsync(alice, "p", Archives.TarGzEntries(
            Archives.File("a.md", T("# a")), Archives.Symlink("b.md", "/etc/passwd")));
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("publish_invalid", await ProblemCodeAsync(r));
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Bad_media_types_and_prefixes_are_refused()
    {
        var alice = await AliceAsync();
        var folder = Folder(("a.md", T("# a")));
        Assert.Equal("unsupported_archive", await ProblemCodeAsync(await PublishAsync(alice, "p", folder, mediaType: "text/plain")));
        Assert.Equal("invalid_path", await ProblemCodeAsync(await PublishAsync(alice, "p", folder, "?prefix=guides")));
    }

    [Fact]
    public async Task Readers_and_read_tokens_cannot_publish()
    {
        var alice = await AliceAsync();
        await AddMemberAsync(alice, "p", "rd", "reader");
        var folder = Folder(("a.md", T("# a")));
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(ClientFor("rd"), "p", folder)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(TokenClient(await CreateTokenAsync(alice, "p", "read")), "p", folder)).StatusCode);
    }

    [Fact]
    public async Task Publishing_is_audited_per_item_and_as_a_whole()
    {
        var alice = await AliceAsync();
        await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), ("b.md", T("# b")))));
        using var scope = Factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<DdmDbContext>().AuditEntries.Select(e => e.Action).ToListAsync();
        Assert.Equal(2, actions.Count(a => a == "doc.create"));
        Assert.Single(actions, a => a == "publish");
    }

    // Review Focus 1
    [Fact]
    public async Task A_publish_racing_single_writes_is_all_or_nothing_and_never_500s()
    {
        var alice = await AliceAsync();
        for (var round = 0; round < 15; round++)
        {
            var path = $"r{round}/a.md";
            await PutDocAsync(alice, "p", path, "# v1");
            var results = await Task.WhenAll(
                PutDocAsync(alice, "p", path, $"# edited {round}", ifMatch: "\"v1\""),
                PublishAsync(alice, "p", Folder(("a.md", T($"# published {round}"))), $"?prefix=r{round}/"));

            Assert.All(results, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
            Assert.Contains(results[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            var history = await ReadAsync<Page<VersionDto>>(await alice.GetAsync($"/api/v1/projects/p/docs/{path}/versions"));
            var expected = 1 + (results[0].StatusCode == HttpStatusCode.OK ? 1 : 0) + (results[1].StatusCode == HttpStatusCode.OK ? 1 : 0);
            Assert.Equal(expected, history.Items.Count);
        }
    }

    // --- carry-forward checks (tombstones, duplicates, dry run, size limits, BOM)

    [Fact]
    public async Task A_dry_run_reports_the_mass_delete_refusal_the_real_call_would_give()
    {
        var alice = await AliceAsync();
        await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), ("b.md", T("# b")))));
        var dry = await PublishAsync(alice, "p", Archives.TarGzEntries(), "?dryRun=true");
        Assert.Equal(HttpStatusCode.Conflict, dry.StatusCode);
        Assert.Equal("publish_mass_delete", await ProblemCodeAsync(dry));
    }

    [Fact]
    public async Task Deleted_items_count_as_absent_so_republishing_revives_them_with_continued_numbering()
    {
        var alice = await AliceAsync();
        var folder = Folder(("a.md", T("# a")), ("b.png", PngBytes), ("apis/pay.yaml", T(Spec)));
        await OkAsync(PublishAsync(alice, "p", folder));
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/docs/a.md")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/assets/b.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/specs/pay")).StatusCode);

        var again = await OkAsync(PublishAsync(alice, "p", folder));
        Assert.Equal(0, again.Unchanged);
        Assert.Empty(again.Updated);
        Assert.Contains(again.Created, i => i is { Type: "document", Key: "a.md", Version: 2 });
        Assert.Contains(again.Created, i => i is { Type: "asset", Key: "b.png" });
        Assert.Contains(again.Created, i => i is { Type: "spec", Key: "pay", Version: 2 });
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
    }

    [Theory]
    [InlineData("A.yaml", "a.yaml")]
    [InlineData("x/api.yaml", "y/api.yaml")]
    public async Task Spec_files_that_lower_case_to_one_name_are_reported_not_thrown(string first, string second)
    {
        var alice = await AliceAsync();
        var r = await PublishAsync(alice, "p", Folder((first, T(Spec)), (second, T(Spec))));
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("publish_invalid", body.GetProperty("code").GetString());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "duplicate_spec_name");
    }

    [Fact]
    public async Task Specs_with_a_byte_order_mark_publish_like_any_other_and_keep_their_bytes()
    {
        var alice = await AliceAsync();
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] yaml = [.. bom, .. T(Spec)];
        byte[] json = [.. bom, .. T("{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"J\",\"version\":\"1\"},\"paths\":{}}")];
        var result = await OkAsync(PublishAsync(alice, "p", Folder(("api.yaml", yaml), ("j.json", json))));
        Assert.Equal(["api", "j"], result.Created.Select(i => i.Key).Order());
        Assert.Equal(yaml, await (await alice.GetAsync("/api/v1/projects/p/specs/api")).Content.ReadAsByteArrayAsync());
        Assert.Equal(json, await (await alice.GetAsync("/api/v1/projects/p/specs/j")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_over_long_openapi_version_is_a_publish_error_not_a_server_error_and_names_the_problem()
    {
        var alice = await AliceAsync();
        var r = await PublishAsync(alice, "p", Folder(("good.md", T("# good")), ("api.yaml", T(Spec.Replace("3.0.3", "\"3.0.300000000000000000\"")))));
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("publish_invalid", body.GetProperty("code").GetString());
        var error = Assert.Single(body.GetProperty("errors").EnumerateArray());
        Assert.Equal("api.yaml", error.GetProperty("path").GetString());
        Assert.Equal("unsupported_openapi_version", error.GetProperty("code").GetString());
        Assert.Contains("found '3.0.300000000000000000'", error.GetProperty("message").GetString());
        Assert.Equal(1, error.GetProperty("line").GetInt32());
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Oversized_archives_get_archive_too_large_whether_or_not_content_length_is_honest()
    {
        var alice = await AliceAsync();
        var small = Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Publish:MaxArchiveBytes"] = "2000" })));
        var client = small.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestAuth.TokenFor("alice"));
        var big = Archives.TarGz(("a.md", RandomBytes(20000)));
        Assert.True(big.Length > 2000);
        var before = SpoolFiles();

        var declared = await PublishAsync(client, "p", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal("archive_too_large", await ProblemCodeAsync(declared));

        var chunked = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects/p/publish") { Content = new UnknownLengthContent(big) };
        chunked.Content.Headers.ContentType = new("application/gzip");
        var undeclared = await client.SendAsync(chunked);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, undeclared.StatusCode);
        Assert.Equal("archive_too_large", await ProblemCodeAsync(undeclared));

        Assert.Equal(before, SpoolFiles());
    }

    [Fact]
    public async Task The_spool_file_is_removed_after_a_successful_publish()
    {
        var alice = await AliceAsync();
        var before = SpoolFiles();
        await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")))));
        Assert.Equal(before, SpoolFiles());
    }

    private static byte[] RandomBytes(int n) => new Random(7).GetItems<byte>(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), n);

    private static int SpoolFiles() => Directory.GetFiles(Path.GetTempPath(), "ddm-publish-*").Length;

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
