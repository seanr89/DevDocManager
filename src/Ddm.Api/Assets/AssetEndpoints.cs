using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Storage;
using Ddm.Api.Tags;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Assets;

public static class AssetEndpoints
{
    private const long MultipartOverhead = 64 * 1024;

    public static void MapAssets(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/projects/{slug}/assets");
        g.MapGet("", ListAsync);
        g.MapPost("", UploadAsync);
        g.MapMethods("{**rest}", [HttpMethods.Get, HttpMethods.Head], GetAsync);
        g.MapPut("{**rest}", PutAsync);
        g.MapDelete("{**rest}", DeleteAsync);
    }

    private static async Task<IResult> PutAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        AssetService assets, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        switch (AssetRoute.Parse(rest))
        {
            case AssetRoute.Tags route:
            {
                var asset = await assets.RequireLiveAsync(access.Project, route.Path, ct);
                var names = await tags.SetFromRequestAsync(caller, access.Project.Id, new ItemRef(ItemType.Asset, asset.Id), asset.Path,
                    await TagEndpoints.ReadBodyAsync(http.Request, ct), ct);
                return Results.Ok(new TagsDto(names));
            }
            case AssetRoute.Current route:
            {
                AssetPath.Require(route.Path); // fail on a bad path or type before reading the body
                var pre = Preconditions.ParseSha(http.Request.Headers);
                RequestBody.AllowUpTo(http, assets.MaxBytes);
                var bytes = await RequestBody.ReadLimitedAsync(http.Request, assets.MaxBytes, $"Assets are limited to {assets.MaxBytes} bytes", ct);
                var result = await assets.WriteAsync(caller, access.Project, assets.Prepare(route.Path, bytes), pre, requireIfMatch: true, ct);
                return await WrittenAsync(http, slug, result, tags, ct);
            }
            default:
                throw ApiException.NotFound("not_found", "No such route");
        }
    }

    private static async Task<IResult> UploadAsync(
        string slug, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, AssetService assets, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (!http.Request.HasFormContentType) throw new ApiException(415, "unsupported_media_type", "Upload assets as multipart/form-data");
        var limit = assets.MaxBytes + MultipartOverhead;
        if (http.Request.ContentLength > limit) throw TooLarge(assets);
        RequestBody.AllowUpTo(http, limit);

        IFormCollection form;
        try { form = await http.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = limit }, ct); }
        catch (InvalidDataException) { throw TooLarge(assets); }
        var path = form["path"].ToString();
        var file = form.Files["file"] ?? throw ApiException.BadRequest("validation_failed", "The request is not valid", "file is required");
        AssetPath.Require(path);
        if (file.Length > assets.MaxBytes) throw TooLarge(assets);
        if (await assets.ExistsAsync(access.Project, path, ct)) throw ApiException.Conflict("asset_exists", $"An asset already exists at {path}");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var result = await assets.WriteAsync(caller, access.Project, assets.Prepare(path, ms.ToArray()),
            new ShaPrecondition(null, false, IfNoneMatchAny: true), requireIfMatch: false, ct);
        return await WrittenAsync(http, slug, result, tags, ct);
    }

    private static async Task<IResult> GetAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        AssetService assets, IBlobStore blobs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        if (AssetRoute.Parse(rest) is not AssetRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        var asset = await assets.RequireLiveAsync(access.Project, route.Path, ct);
        return await AssetResponses.ServeAsync(http, blobs, asset.StorageKey, asset.ContentType, asset.Size, asset.Sha256,
            AssetResponses.FileNameOf(asset.Path), "private, no-cache", ct);
    }

    private static async Task<IResult> ListAsync(
        string slug, string? prefix, bool? deleted, string[]? tag, string? cursor, int? limit, ClaimsPrincipal user,
        ProjectAuthorizer authz, DdmDbContext db, TagService tags, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);
        if (prefix is { Length: > ContentPath.MaxLength })
            throw ApiException.BadRequest("validation_failed", "The request is not valid", "prefix is too long");
        var filter = TagName.Filter(tag);

        var query = db.Assets.AsNoTracking().Where(a => a.ProjectId == access.Project.Id);
        query = deleted == true ? query.Where(a => a.DeletedAt != null) : query.Where(a => a.DeletedAt == null);
        if (!string.IsNullOrEmpty(prefix)) query = query.Where(a => a.Path.StartsWith(prefix));
        if (filter.Count > 0)
        {
            var tagged = tags.ItemsWithAll(ItemType.Asset, filter);
            query = query.Where(a => tagged.Contains(a.Id));
        }
        if (after is not null) query = query.Where(a => string.Compare(a.Path, after) > 0);

        var rows = await query.OrderBy(a => a.Path).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, a => a.Path);
        var tagMap = await tags.TagsForAsync(ItemType.Asset, page.Items.Select(a => a.Id).ToList(), ct);
        return Results.Ok(new Page<AssetDto>(page.Items.Select(a => AssetDto.From(a, tagMap[a.Id])).ToList(), page.Next));
    }

    private static async Task<IResult> DeleteAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, AssetService assets, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (AssetRoute.Parse(rest) is not AssetRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        await assets.DeleteAsync(caller, access.Project, route.Path, Preconditions.ParseSha(http.Request.Headers), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> WrittenAsync(HttpContext http, string slug, AssetWriteResult r, TagService tags, CancellationToken ct)
    {
        http.Response.Headers.ETag = Preconditions.ShaETag(r.Asset.Sha256);
        var dto = AssetDto.From(r.Asset, await tags.TagsForAsync(new ItemRef(ItemType.Asset, r.Asset.Id), ct));
        return r.Created ? Results.Created($"/api/v1/projects/{slug}/assets/{r.Asset.Path}", dto) : Results.Ok(dto);
    }

    private static ApiException TooLarge(AssetService assets) => ApiException.PayloadTooLarge($"Assets are limited to {assets.MaxBytes} bytes");
}
