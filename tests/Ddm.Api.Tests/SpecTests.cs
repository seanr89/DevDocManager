using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Specs;
using Ddm.Api.Tags;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class SpecTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private const string V1 = "openapi: 3.0.3\ninfo: {title: Pets, version: '1'}\npaths:\n  /pets:\n    get:\n      responses:\n        '200': {description: ok}\n";
    private static readonly string V2 = V1.Replace("version: '1'", "version: '2'");

    private async Task<HttpClient> AliceAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        return alice;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Put_creates_a_spec_and_get_returns_the_original_bytes()
    {
        var alice = await AliceAsync();
        var r = await PutSpecAsync(alice, "p", "pets", V1);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
        var dto = await ReadAsync<SpecDto>(r);
        Assert.Equal(("Pets", "1", "3.0.3", "yaml", 1, 1), (dto.Title, dto.ApiVersion, dto.OpenApiVersion, dto.Format, dto.Version, dto.OperationCount));

        var get = await alice.GetAsync("/api/v1/projects/p/specs/pets");
        Assert.Equal("application/yaml", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal(V1, await get.Content.ReadAsStringAsync());
        Assert.Equal("\"v1\"", get.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Normalized_json_and_operations_are_served()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        var normalized = await JsonAsync(await alice.GetAsync("/api/v1/projects/p/specs/pets/normalized"));
        Assert.Equal("Pets", normalized.GetProperty("info").GetProperty("title").GetString());
        var ops = await JsonAsync(await alice.GetAsync("/api/v1/projects/p/specs/pets/operations"));
        Assert.Equal("get", ops[0].GetProperty("method").GetString());
        Assert.Equal("/pets", ops[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Invalid_specs_are_422_with_errors_and_nothing_is_stored()
    {
        var alice = await AliceAsync();
        var r = await PutSpecAsync(alice, "p", "pets", "openapi: 3.0.3\ninfo: {version: '1'}\npaths: {}\n");
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        var body = await JsonAsync(r);
        Assert.Equal("invalid_spec", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").GetArrayLength() > 0);
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Updates_need_if_match_and_identical_content_is_a_no_op()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        Assert.Equal((HttpStatusCode)428, (await PutSpecAsync(alice, "p", "pets", V2)).StatusCode);
        Assert.Equal("\"v1\"", (await PutSpecAsync(alice, "p", "pets", V1, ifMatch: "\"v1\"")).Headers.ETag!.Tag);
        var r = await PutSpecAsync(alice, "p", "pets", V2, ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("\"v2\"", r.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutSpecAsync(alice, "p", "pets", V1, ifMatch: "\"v1\"")).StatusCode);
    }

    [Fact]
    public async Task History_old_versions_and_restore()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        await PutSpecAsync(alice, "p", "pets", V2, ifMatch: "\"v1\"");

        var history = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/specs/pets/versions"));
        Assert.Equal([2, 1], history.Items.Select(v => v.Number));
        Assert.Equal(V1, await (await alice.GetAsync("/api/v1/projects/p/specs/pets/versions/1")).Content.ReadAsStringAsync());
        var old = await JsonAsync(await alice.GetAsync("/api/v1/projects/p/specs/pets/versions/1/normalized"));
        Assert.Equal("1", old.GetProperty("info").GetProperty("version").GetString());

        var restored = await alice.PostAsync("/api/v1/projects/p/specs/pets/versions/1/restore", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("\"v3\"", restored.Headers.ETag!.Tag);
        Assert.Equal(V1, await (await alice.GetAsync("/api/v1/projects/p/specs/pets")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delete_tombstones_and_a_put_revives_with_the_next_version()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/specs/pets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p/specs/pets")).StatusCode);
        Assert.Empty((await ReadAsync<Page<SpecDto>>(await alice.GetAsync("/api/v1/projects/p/specs"))).Items);
        Assert.Single((await ReadAsync<Page<SpecDto>>(await alice.GetAsync("/api/v1/projects/p/specs?deleted=true"))).Items);
        var r = await PutSpecAsync(alice, "p", "pets", V2);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v2\"", r.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Post_creates_from_json_and_refuses_an_existing_name()
    {
        var alice = await AliceAsync();
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/specs", new { name = "pets", content = V1, message = "first" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var again = await alice.PostAsJsonAsync("/api/v1/projects/p/specs", new { name = "pets", content = V1 });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("spec_exists", await ProblemCodeAsync(again));
    }

    [Fact]
    public async Task A_spec_with_a_byte_order_mark_is_accepted_and_stored_byte_for_byte()
    {
        var alice = await AliceAsync();
        var yaml = await PutSpecAsync(alice, "p", "pets", "\uFEFF" + V1);
        Assert.Equal(HttpStatusCode.Created, yaml.StatusCode);
        Assert.Equal("Pets", (await ReadAsync<SpecDto>(yaml)).Title);
        Assert.Equal(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(V1)), await (await alice.GetAsync("/api/v1/projects/p/specs/pets")).Content.ReadAsByteArrayAsync());

        var json = "\uFEFF{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"J\",\"version\":\"1\"},\"paths\":{}}";
        var r = await PutSpecAsync(alice, "p", "j", json, mediaType: "application/json");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("json", (await ReadAsync<SpecDto>(r)).Format);
    }

    [Theory]
    [InlineData("3.0.300000000000000000")]
    [InlineData("3.0.\u0663")]
    public async Task An_over_long_or_non_ascii_openapi_version_is_422_not_500(string version)
    {
        var alice = await AliceAsync();
        var r = await PutSpecAsync(alice, "p", "pets", V1.Replace("3.0.3", $"\"{version}\""));
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        var body = await JsonAsync(r);
        Assert.Equal("unsupported_openapi_version", body.GetProperty("code").GetString());
        var error = body.GetProperty("errors")[0];
        Assert.Equal("#/openapi", error.GetProperty("pointer").GetString());
        Assert.Equal(1, error.GetProperty("line").GetInt32());
        Assert.Contains("found '", error.GetProperty("message").GetString());
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Bad_names_and_media_types_are_refused()
    {
        var alice = await AliceAsync();
        var badName = await PutSpecAsync(alice, "p", "Bad_Name", V1);
        Assert.Equal("invalid_spec_name", await ProblemCodeAsync(badName));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await PutSpecAsync(alice, "p", "pets", V1, mediaType: "text/plain")).StatusCode);
    }

    [Fact]
    public async Task Json_specs_keep_their_format()
    {
        var alice = await AliceAsync();
        var json = "{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"J\",\"version\":\"1\"},\"paths\":{}}";
        Assert.Equal("json", (await ReadAsync<SpecDto>(await PutSpecAsync(alice, "p", "j", json, mediaType: "application/json"))).Format);
        Assert.Equal("application/json", (await alice.GetAsync("/api/v1/projects/p/specs/j")).Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Spec_tags_can_be_set_and_filtered()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        await PutSpecAsync(alice, "p", "other", V1);
        Assert.Equal(["public"], (await ReadAsync<TagsDto>(await alice.PutAsJsonAsync("/api/v1/projects/p/specs/pets/tags", new { tags = new[] { "public" } }))).Tags);
        var page = await ReadAsync<Page<SpecDto>>(await alice.GetAsync("/api/v1/projects/p/specs?tag=public"));
        Assert.Equal(["pets"], page.Items.Select(s => s.Name));
    }

    [Fact]
    public async Task Readers_cannot_write_specs()
    {
        var alice = await AliceAsync();
        await AddMemberAsync(alice, "p", "rd", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await PutSpecAsync(ClientFor("rd"), "p", "pets", V1)).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_project_removes_spec_versions()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p")).StatusCode);
        using var scope = Factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<DdmDbContext>().Versions.CountAsync());
    }
}
