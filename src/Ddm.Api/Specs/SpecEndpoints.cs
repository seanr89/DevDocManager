using System.Security.Claims;
using System.Text;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Tags;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Specs;

public static class SpecEndpoints
{
    private static readonly string[] SpecMediaTypes = ["application/yaml", "application/x-yaml", "text/yaml", "application/json"];

    public static void MapSpecs(this RouteGroupBuilder v1)
    {
        // Spec names contain no '/', so plain route segments suffice (no catch-all).
        var g = v1.MapGroup("/projects/{slug}/specs");
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("{name}", GetAsync);
        g.MapPut("{name}", PutAsync);
        g.MapDelete("{name}", DeleteAsync);
        g.MapGet("{name}/normalized", GetNormalizedAsync);
        g.MapGet("{name}/operations", GetOperationsAsync);
        g.MapPut("{name}/tags", PutTagsAsync);
        g.MapGet("{name}/versions", ListVersionsAsync);
        g.MapGet("{name}/versions/{n:int}", GetVersionAsync);
        g.MapGet("{name}/versions/{n:int}/normalized", GetVersionNormalizedAsync);
        g.MapPost("{name}/versions/{n:int}/restore", RestoreAsync);
    }

    private static async Task<IResult> CreateAsync(
        string slug, CreateSpecRequest? body, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        SpecService specs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var name = SpecName.Require(body?.Name);
        if (body!.Content is null) throw ApiException.BadRequest("validation_failed", "The request is not valid", "content is required");
        if (body.Message is { Length: > 500 }) throw ApiException.BadRequest("validation_failed", "The request is not valid", "message is limited to 500 characters");
        if (await specs.FindAsync(access.Project, name, ct) is { IsLive: true })
            throw ApiException.Conflict("spec_exists", $"A spec named {name} already exists");

        var result = await specs.WriteAsync(caller, access.Project, await specs.PrepareAsync(name, Encoding.UTF8.GetBytes(body.Content), ct),
            body.Message, new WritePrecondition(null, false, IfNoneMatchAny: true), requireIfMatch: false, ct);
        return await WrittenAsync(http, slug, result, tags, created: true, ct);
    }

    private static async Task<IResult> PutAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        SpecService specs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        SpecName.Require(name);
        var type = http.Request.GetTypedHeaders().ContentType?.MediaType.Value?.ToLowerInvariant();
        if (type is null || !SpecMediaTypes.Contains(type))
            throw new ApiException(415, "unsupported_media_type", "Send specs as application/yaml or application/json");
        var pre = Preconditions.Parse(http.Request.Headers);
        var message = DocumentEndpoints.MessageFrom(http.Request);
        RequestBody.AllowUpTo(http, specs.MaxBytes);
        var bytes = await RequestBody.ReadLimitedAsync(http.Request, specs.MaxBytes, $"Specs are limited to {specs.MaxBytes} bytes", ct);

        var result = await specs.WriteAsync(caller, access.Project, await specs.PrepareAsync(name, bytes, ct), message, pre, requireIfMatch: true, ct);
        return await WrittenAsync(http, slug, result, tags, result.Created, ct);
    }

    private static async Task<IResult> GetAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var (spec, version) = await specs.GetCurrentAsync(access.Project, name, ct);
        return Original(http, spec.Format, version, await specs.ReadAsync(version.ContentRef, ct));
    }

    private static async Task<IResult> GetNormalizedAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var (_, version) = await specs.GetCurrentAsync(access.Project, name, ct);
        return Normalized(http, version, await specs.ReadAsync(version.NormalizedRef!, ct));
    }

    private static async Task<IResult> GetOperationsAsync(
        string slug, string name, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var spec = await specs.RequireAsync(access.Project, name, includeDeleted: false, ct);
        return Results.Text(spec.Operations, "application/json");
    }

    private static async Task<IResult> PutTagsAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        SpecService specs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var spec = await specs.RequireAsync(access.Project, name, includeDeleted: false, ct);
        var names = await tags.SetFromRequestAsync(caller, access.Project.Id, new ItemRef(ItemType.Spec, spec.Id), spec.Name,
            await TagEndpoints.ReadBodyAsync(http.Request, ct), ct);
        return Results.Ok(new TagsDto(names));
    }

    private static async Task<IResult> ListVersionsAsync(
        string slug, string name, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        return Results.Ok(await specs.ListVersionsAsync(access.Project, name, limit, cursor, ct));
    }

    private static async Task<IResult> GetVersionAsync(
        string slug, string name, int n, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var version = await specs.GetVersionAsync(access.Project, name, n, ct);
        var bytes = await specs.ReadAsync(version.ContentRef, ct);
        // An old version may predate a format change, so its format comes from its own bytes, not the current row.
        return Original(http, SpecValidator.FormatOf(Encoding.UTF8.GetString(bytes)), version, bytes);
    }

    private static async Task<IResult> GetVersionNormalizedAsync(
        string slug, string name, int n, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var version = await specs.GetVersionAsync(access.Project, name, n, ct);
        return Normalized(http, version, await specs.ReadAsync(version.NormalizedRef!, ct));
    }

    private static async Task<IResult> RestoreAsync(
        string slug, string name, int n, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        SpecService specs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var result = await specs.RestoreAsync(caller, access.Project, name, n, Preconditions.Parse(http.Request.Headers), ct);
        return await WrittenAsync(http, slug, result, tags, created: false, ct);
    }

    private static async Task<IResult> DeleteAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        await specs.DeleteAsync(caller, access.Project, name, Preconditions.Parse(http.Request.Headers), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(
        string slug, bool? deleted, string[]? tag, string? cursor, int? limit, ClaimsPrincipal user,
        ProjectAuthorizer authz, DdmDbContext db, TagService tags, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);
        var filter = TagName.Filter(tag);

        var query = db.Specs.AsNoTracking().Where(s => s.ProjectId == access.Project.Id);
        query = deleted == true ? query.Where(s => s.DeletedAt != null) : query.Where(s => s.DeletedAt == null);
        if (filter.Count > 0)
        {
            var tagged = tags.ItemsWithAll(ItemType.Spec, filter);
            query = query.Where(s => tagged.Contains(s.Id));
        }
        if (after is not null) query = query.Where(s => string.Compare(s.Name, after) > 0);

        var rows = await (from s in query
                          join v in db.Versions on s.CurrentVersionId equals (Guid?)v.Id
                          orderby s.Name
                          select new { Spec = s, v.Number })
            .Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, r => r.Spec.Name);
        var tagMap = await tags.TagsForAsync(ItemType.Spec, page.Items.Select(r => r.Spec.Id).ToList(), ct);
        return Results.Ok(new Page<SpecDto>(page.Items.Select(r => SpecDto.From(r.Spec, r.Number, tagMap[r.Spec.Id])).ToList(), page.Next));
    }

    private static IResult Original(HttpContext http, string format, ContentVersion version, byte[] bytes)
    {
        http.Response.Headers.ETag = Preconditions.ETag(version.Number);
        http.Response.Headers.CacheControl = "private, no-cache";
        return Results.Bytes(bytes, SpecService.MediaTypeFor(format));
    }

    private static IResult Normalized(HttpContext http, ContentVersion version, byte[] json)
    {
        http.Response.Headers.ETag = Preconditions.ETag(version.Number);
        http.Response.Headers.CacheControl = "private, no-cache";
        return Results.Bytes(json, "application/json");
    }

    private static async Task<IResult> WrittenAsync(HttpContext http, string slug, SpecWriteResult r, TagService tags, bool created, CancellationToken ct)
    {
        http.Response.Headers.ETag = Preconditions.ETag(r.Version.Number);
        var dto = SpecDto.From(r.Spec, r.Version.Number, await tags.TagsForAsync(new ItemRef(ItemType.Spec, r.Spec.Id), ct));
        return created ? Results.Created($"/api/v1/projects/{slug}/specs/{r.Spec.Name}", dto) : Results.Ok(dto);
    }
}
