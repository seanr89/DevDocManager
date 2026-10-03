using System.Security.Claims;
using System.Text;
using Ddm.Api.Common;
using Ddm.Api.Domain;
using Ddm.Api.Identity;

namespace Ddm.Api.Documents;

public static class DocumentEndpoints
{
    public static void MapDocuments(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/projects/{slug}/docs");
        g.MapPost("", CreateAsync);
        g.MapGet("{**rest}", GetAsync);
        g.MapPut("{**rest}", PutAsync);
    }

    private static async Task<IResult> CreateAsync(
        string slug, CreateDocumentRequest? body, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, DocumentService docs, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var path = DocumentPath.Require(body?.Path);
        if (body!.Content is null) throw ApiException.BadRequest("validation_failed", "The request is not valid", "content is required");
        if (body.Message is { Length: > 500 }) throw ApiException.BadRequest("validation_failed", "The request is not valid", "message is limited to 500 characters");
        if (await docs.ExistsAsync(access.Project, path, ct))
            throw ApiException.Conflict("document_exists", $"A document already exists at {path}");

        var result = await docs.WriteAsync(caller, access.Project, path, Encoding.UTF8.GetBytes(body.Content), body.Message,
            new WritePrecondition(null, false, IfNoneMatchAny: true), requireIfMatch: false, ct);
        return Created(http, slug, result);
    }

    private static async Task<IResult> GetAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, DocumentService docs, MarkdownRenderer renderer, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        if (DocRoute.Parse(rest) is not DocRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        var path = DocumentPath.Require(route.Path);
        var (_, version) = await docs.GetCurrentAsync(access.Project, path, ct);
        return await RespondAsync(http, docs, renderer, path, version, ct);
    }

    private static async Task<IResult> PutAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, DocumentService docs, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (DocRoute.Parse(rest) is not DocRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        var path = DocumentPath.Require(route.Path);
        var pre = Preconditions.Parse(http.Request.Headers);
        var message = MessageFrom(http.Request);
        var bytes = await ReadBodyAsync(http.Request, ct);

        var result = await docs.WriteAsync(caller, access.Project, path, bytes, message, pre, requireIfMatch: true, ct);
        return result.Created ? Created(http, slug, result) : Ok(http, result);
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

    internal static IResult Created(HttpContext http, string slug, WriteResult r)
    {
        http.Response.Headers.ETag = Preconditions.ETag(r.Version.Number);
        return Results.Created($"/api/v1/projects/{slug}/docs/{r.Path}", DocumentDto.From(r.Path, r.Parsed, r.Version));
    }

    internal static IResult Ok(HttpContext http, WriteResult r)
    {
        http.Response.Headers.ETag = Preconditions.ETag(r.Version.Number);
        return Results.Ok(DocumentDto.From(r.Path, r.Parsed, r.Version));
    }

    internal static async Task<IResult> RespondAsync(
        HttpContext http, DocumentService docs, MarkdownRenderer renderer, string path, ContentVersion version, CancellationToken ct)
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
            DocFormat.Json => Results.Ok(DocumentDto.From(path, parsed, version)),
            _ => Results.Text(text, "text/markdown; charset=utf-8"),
        };
    }
}
