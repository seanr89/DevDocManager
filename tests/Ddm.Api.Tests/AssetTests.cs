using Ddm.Api.Assets;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Tags;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class AssetTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<HttpClient> AliceAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        return alice;
    }

    private static string ETagOf(byte[] bytes) => $"\"{Sha(bytes)}\"";

    [Fact]
    public async Task Put_creates_an_asset_and_get_streams_it_back_with_safe_headers()
    {
        var alice = await AliceAsync();
        var r = await PutAssetAsync(alice, "p", "images/a.png", PngBytes);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(ETagOf(PngBytes), r.Headers.ETag!.Tag);
        var dto = await ReadAsync<AssetDto>(r);
        Assert.Equal(("images/a.png", "image/png", (long)PngBytes.Length), (dto.Path, dto.ContentType, dto.Size));

        var get = await alice.GetAsync("/api/v1/projects/p/assets/images/a.png");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(PngBytes, await get.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(AssetResponses.Csp, get.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Null(get.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task Head_returns_headers_without_a_body()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        var head = await alice.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/v1/projects/p/assets/a.png"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(PngBytes.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task If_none_match_with_the_current_etag_is_304()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects/p/assets/a.png");
        request.Headers.TryAddWithoutValidation("If-None-Match", ETagOf(PngBytes));
        Assert.Equal(HttpStatusCode.NotModified, (await alice.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Replacing_needs_the_current_etag_and_identical_bytes_are_a_no_op()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        Assert.Equal((HttpStatusCode)428, (await PutAssetAsync(alice, "p", "a.png", PngVariant(1))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutAssetAsync(alice, "p", "a.png", PngVariant(1), ifMatch: ETagOf(PngVariant(9)))).StatusCode);

        var same = await PutAssetAsync(alice, "p", "a.png", PngBytes, ifMatch: ETagOf(PngBytes));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        var replaced = await PutAssetAsync(alice, "p", "a.png", PngVariant(1), ifMatch: ETagOf(PngBytes));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(ETagOf(PngVariant(1)), replaced.Headers.ETag!.Tag);
        Assert.Equal(PngVariant(1), await (await alice.GetAsync("/api/v1/projects/p/assets/a.png")).Content.ReadAsByteArrayAsync());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
        Assert.Equal(2, await db.AuditEntries.CountAsync(e => e.Action.StartsWith("asset.")));
    }

    [Fact]
    public async Task Post_multipart_creates_and_refuses_an_existing_path()
    {
        var alice = await AliceAsync();
        MultipartFormDataContent Form() => new()
        {
            { new StringContent("logo.png"), "path" },
            { new ByteArrayContent(PngBytes), "file", "logo.png" },
        };
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsync("/api/v1/projects/p/assets", Form())).StatusCode);
        var again = await alice.PostAsync("/api/v1/projects/p/assets", Form());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("asset_exists", await ProblemCodeAsync(again));
    }

    [Fact]
    public async Task A_png_renamed_svg_is_rejected_and_nothing_is_stored()
    {
        var alice = await AliceAsync();
        var r = await PutAssetAsync(alice, "p", "a.svg", PngBytes);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("asset_type_mismatch", await ProblemCodeAsync(r));
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Theory]
    [InlineData("tool.exe", 415, "unsupported_asset_type")]
    [InlineData("page.md", 400, "invalid_path")]
    public async Task Disallowed_paths_are_refused(string path, int status, string code)
    {
        var alice = await AliceAsync();
        var r = await PutAssetAsync(alice, "p", path, PngBytes);
        Assert.Equal((HttpStatusCode)status, r.StatusCode);
        Assert.Equal(code, await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Oversized_uploads_are_413()
    {
        var alice = await AliceAsync();
        var big = new byte[10 * 1024 * 1024 + 1];
        PngBytes.CopyTo(big, 0);
        var r = await PutAssetAsync(alice, "p", "big.png", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal("payload_too_large", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Svg_and_text_are_served_inertly()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "x.svg", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"));
        await PutAssetAsync(alice, "p", "notes.txt", Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"));

        // Script-bearing SVG is accepted by design, so the response headers are what keep it inert.
        var svg = await alice.GetAsync("/api/v1/projects/p/assets/x.svg");
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType!.MediaType);
        Assert.Equal(AssetResponses.Csp, svg.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("sandbox", svg.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", svg.Headers.GetValues("X-Content-Type-Options").Single());

        var txt = await alice.GetAsync("/api/v1/projects/p/assets/notes.txt");
        Assert.Equal("text/plain", txt.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", txt.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", txt.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(AssetResponses.Csp, txt.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Svg_and_text_keep_the_safe_headers_on_head_and_304()
    {
        var alice = await AliceAsync();
        var svgBytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        await PutAssetAsync(alice, "p", "x.svg", svgBytes);
        var head = await alice.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/v1/projects/p/assets/x.svg"));
        Assert.Equal(AssetResponses.Csp, head.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", head.Headers.GetValues("X-Content-Type-Options").Single());

        var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects/p/assets/x.svg");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", ETagOf(svgBytes));
        var notModified = await alice.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(AssetResponses.Csp, notModified.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", notModified.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Delete_tombstones_and_the_list_shows_it_with_deleted_true()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        await PutAssetAsync(alice, "p", "b.png", PngBytes);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
        var live = await ReadAsync<Page<AssetDto>>(await alice.GetAsync("/api/v1/projects/p/assets"));
        Assert.Equal(["b.png"], live.Items.Select(a => a.Path));
        var deleted = await ReadAsync<Page<AssetDto>>(await alice.GetAsync("/api/v1/projects/p/assets?deleted=true"));
        Assert.Equal(["a.png"], deleted.Items.Select(a => a.Path));
        Assert.Equal(HttpStatusCode.Created, (await PutAssetAsync(alice, "p", "a.png", PngBytes)).StatusCode);
    }

    [Fact]
    public async Task Readers_can_read_but_not_write()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        await AddMemberAsync(alice, "p", "rd", "reader");
        var reader = ClientFor("rd");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAssetAsync(reader, "p", "b.png", PngBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("stranger").GetAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
    }

    [Fact]
    public async Task Asset_tags_can_be_set_and_filtered()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        await PutAssetAsync(alice, "p", "b.png", PngBytes);
        var r = await alice.PutAsJsonAsync("/api/v1/projects/p/assets/a.png/tags", new { tags = new[] { "Logo" } });
        Assert.Equal(["logo"], (await ReadAsync<TagsDto>(r)).Tags);
        var page = await ReadAsync<Page<AssetDto>>(await alice.GetAsync("/api/v1/projects/p/assets?tag=logo"));
        Assert.Equal(["a.png"], page.Items.Select(a => a.Path));
        Assert.Equal(["logo"], page.Items[0].Tags);
    }

    [Fact]
    public async Task A_storage_outage_fails_the_upload_and_stores_nothing()
    {
        var alice = await AliceAsync();
        Factory.Blobs.FailNextPut = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await PutAssetAsync(alice, "p", "a.png", PngBytes)).StatusCode);
        Assert.Empty((await ReadAsync<Page<AssetDto>>(await alice.GetAsync("/api/v1/projects/p/assets"))).Items);
    }
}
