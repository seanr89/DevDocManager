using System.Security.Claims;
using System.Text;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Tags;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Documents;

public static class DocumentEndpoints
{
    public static void MapDocuments(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/projects/{slug}/docs");
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("{**rest}", GetAsync);
        g.MapPut("{**rest}", PutAsync);
        g.MapDelete("{**rest}", DeleteAsync);
        g.MapPost("{**rest}", RestoreAsync);
    }

    private static async Task<IResult> CreateAsync(
        string slug, CreateDocumentRequest? body, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, DocumentService docs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var path = DocumentPath.Require(body?.Path);
        if (body!.Content is null) throw ApiException.BadRequest("validation_failed", "The request is not valid", "content is required");
        if (body.Message is { Length: > 500 }) throw ApiException.BadRequest("validation_failed", "The request is not valid", "message is limited to 500 characters");
        if (await docs.ExistsAsync(access.Project, path, ct))
            throw ApiException.Conflict("document_exists", $"A document already exists at {path}");

        var result = await docs.WriteAsync(caller, access.Project, DocumentService.Prepare(path, Encoding.UTF8.GetBytes(body.Content)),
            body.Message, new WritePrecondition(null, false, IfNoneMatchAny: true), requireIfMatch: false, ct);
        return await WrittenAsync(http, slug, result, tags, created: true, ct);
    }

    private static async Task<IResult> GetAsync(
        string slug, string rest, string? cursor, int? limit, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, DocumentService docs, MarkdownRenderer renderer, TagService tags, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        switch (DocRoute.Parse(rest))
        {
            case DocRoute.Current c:
            {
                var path = DocumentPath.Require(c.Path);
                var (_, version) = await docs.GetCurrentAsync(access.Project, path, ct);
                return await RespondAsync(http, docs, renderer, tags, path, version, ct);
            }
            case DocRoute.History h:
                return Results.Ok(await docs.ListVersionsAsync(access.Project, DocumentPath.Require(h.Path), limit, cursor, ct));
            case DocRoute.Snapshot s:
            {
                var path = DocumentPath.Require(s.Path);
                var version = await docs.GetVersionAsync(access.Project, path, s.Number, ct);
                return await RespondAsync(http, docs, renderer, tags, path, version, ct);
            }
            default:
                throw ApiException.NotFound("not_found", "No such route");
        }
    }

    private static async Task<IResult> PutAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        DocumentService docs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        switch (DocRoute.Parse(rest))
        {
            case DocRoute.Current route:
            {
                var path = DocumentPath.Require(route.Path);
                var pre = Preconditions.Parse(http.Request.Headers);
                var message = MessageFrom(http.Request);
                var bytes = await ReadBodyAsync(http.Request, ct);
                var result = await docs.WriteAsync(caller, access.Project, DocumentService.Prepare(path, bytes), message, pre, requireIfMatch: true, ct);
                return await WrittenAsync(http, slug, result, tags, result.Created, ct);
            }
            case DocRoute.Tags route:
            {
                var doc = await docs.RequireDocumentAsync(access.Project, DocumentPath.Require(route.Path), includeDeleted: false, ct);
                if (TagName.DeclaredInFrontMatter(doc.FrontMatter))
                    throw ApiException.Conflict("tags_managed_by_front_matter",
                        "This document's tags come from its front matter; change the tags key in the document instead");
                var names = await tags.SetFromRequestAsync(caller, access.Project.Id, new ItemRef(ItemType.Document, doc.Id), doc.Path,
                    await TagEndpoints.ReadBodyAsync(http.Request, ct), ct);
                return Results.Ok(new TagsDto(names));
            }
            default:
                throw ApiException.NotFound("not_found", "No such route");
        }
    }

    private static async Task<IResult> ListAsync(
        string slug, string? prefix, bool? deleted, string[]? tag, string? cursor, int? limit, ClaimsPrincipal user,
        ProjectAuthorizer authz, DdmDbContext db, TagService tags, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);
        if (prefix is { Length: > DocumentPath.MaxLength })
            throw ApiException.BadRequest("validation_failed", "The request is not valid", "prefix is too long");
        var filter = TagName.Filter(tag);

        var docsQuery = db.Documents.Where(d => d.ProjectId == access.Project.Id);
        docsQuery = deleted == true ? docsQuery.Where(d => d.DeletedAt != null) : docsQuery.Where(d => d.DeletedAt == null);
        if (!string.IsNullOrEmpty(prefix)) docsQuery = docsQuery.Where(d => d.Path.StartsWith(prefix));
        if (filter.Count > 0)
        {
            var tagged = tags.ItemsWithAll(ItemType.Document, filter);
            docsQuery = docsQuery.Where(d => tagged.Contains(d.Id));
        }
        if (after is not null) docsQuery = docsQuery.Where(d => string.Compare(d.Path, after) > 0);

        var rows = await (from d in docsQuery
                          join v in db.Versions on d.CurrentVersionId equals (Guid?)v.Id
                          orderby d.Path
                          select new { d.Id, d.Path, d.Title, v.Number, d.UpdatedAt })
            .Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, d => d.Path);
        var tagMap = await tags.TagsForAsync(ItemType.Document, page.Items.Select(d => d.Id).ToList(), ct);
        var items = page.Items.Select(d => new DocumentSummaryDto(d.Path, d.Title, d.Number, d.UpdatedAt, tagMap[d.Id])).ToList();
        return Results.Ok(new Page<DocumentSummaryDto>(items, page.Next));
    }

    private static async Task<IResult> DeleteAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, DocumentService docs, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (DocRoute.Parse(rest) is not DocRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        await docs.DeleteAsync(caller, access.Project, DocumentPath.Require(route.Path), Preconditions.Parse(http.Request.Headers), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RestoreAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        DocumentService docs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (DocRoute.Parse(rest) is not DocRoute.Restore route) throw ApiException.NotFound("not_found", "No such route");
        var result = await docs.RestoreAsync(caller, access.Project, DocumentPath.Require(route.Path), route.Number,
            Preconditions.Parse(http.Request.Headers), ct);
        return await WrittenAsync(http, slug, result, tags, created: false, ct);
    }

    // --- shared helpers ---

    internal static string? MessageFrom(HttpRequest request)
    {
        string? message = request.Query["message"];
        if (message is { Length: > 500 })
            throw ApiException.BadRequest("validation_failed", "The request is not valid", "message is limited to 500 characters");
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }

    internal static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        var type = request.GetTypedHeaders().ContentType?.MediaType.Value?.ToLowerInvariant();
        if (type is not ("text/markdown" or "text/plain"))
            throw new ApiException(415, "unsupported_media_type", "Send documents as text/markdown");
        if (request.ContentLength > DocumentService.MaxBytes) throw TooLarge();

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > DocumentService.MaxBytes) throw TooLarge();
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static ApiException TooLarge() => ApiException.PayloadTooLarge($"Documents are limited to {DocumentService.MaxBytes} bytes");

    private static async Task<IResult> WrittenAsync(HttpContext http, string slug, WriteResult r, TagService tags, bool created, CancellationToken ct)
    {
        http.Response.Headers.ETag = Preconditions.ETag(r.Version.Number);
        var dto = DocumentDto.From(r.Path, r.Parsed, r.Version, await tags.TagsForAsync(new ItemRef(ItemType.Document, r.DocumentId), ct));
        return created ? Results.Created($"/api/v1/projects/{slug}/docs/{r.Path}", dto) : Results.Ok(dto);
    }

    internal static async Task<IResult> RespondAsync(
        HttpContext http, DocumentService docs, MarkdownRenderer renderer, TagService tags, string path, ContentVersion version, CancellationToken ct)
    {
        var format = ContentNegotiation.Choose(http.Request.GetTypedHeaders().Accept)
            ?? throw new ApiException(406, "not_acceptable", "Not acceptable", "Supported: text/markdown, text/html, application/json");
        http.Response.Headers.ETag = Preconditions.ETag(version.Number);
        http.Response.Headers.Vary = "Accept";
        http.Response.Headers.CacheControl = "private, no-cache";

        var text = DocumentService.StrictUtf8.GetString(await docs.ReadContentAsync(version, ct));
        var parsed = FrontMatter.Parse(text, path);
        return format switch
        {
            DocFormat.Html => Results.Text(renderer.ToHtml(parsed.Body), "text/html; charset=utf-8"),
            DocFormat.Json => Results.Ok(DocumentDto.From(path, parsed, version,
                await tags.TagsForAsync(new ItemRef(ItemType.Document, version.ItemId), ct))),
            _ => Results.Text(text, "text/markdown; charset=utf-8"),
        };
    }
}
