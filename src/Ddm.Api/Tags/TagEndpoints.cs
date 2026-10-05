using System.Security.Claims;
using System.Text.Json;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Tags;

public static class TagEndpoints
{
    public static void MapTags(this RouteGroupBuilder v1)
    {
        v1.MapGet("/projects/{slug}/tags", ListAsync);
        v1.MapDelete("/projects/{slug}/tags/{name}", DeleteAsync);
    }

    /// <summary>Reads a <c>{"tags": [...]}</c> body for the per-item tag routes.</summary>
    public static async Task<SetTagsRequest?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType()) throw new ApiException(415, "unsupported_media_type", "Send tags as application/json");
        try { return await request.ReadFromJsonAsync<SetTagsRequest>(ct); }
        catch (JsonException) { throw ApiException.BadRequest("validation_failed", "The request is not valid", "The body is not valid JSON"); }
    }

    private static async Task<IResult> ListAsync(
        string slug, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);

        // Count only assignments on live items, so a tombstone's tags do not inflate the vocabulary.
        var live = db.TagAssignments.Where(a =>
            a.ItemType == ItemType.Project
            || (a.ItemType == ItemType.Document && db.Documents.Any(d => d.Id == a.ItemId && d.DeletedAt == null))
            || (a.ItemType == ItemType.Asset && db.Assets.Any(x => x.Id == a.ItemId && x.DeletedAt == null))
            || (a.ItemType == ItemType.Spec && db.Specs.Any(s => s.Id == a.ItemId && s.DeletedAt == null)));
        var query = db.Tags.Where(t => t.ProjectId == access.Project.Id);
        if (after is not null) query = query.Where(t => string.Compare(t.Name, after) > 0);
        var rows = await query.OrderBy(t => t.Name)
            .Select(t => new TagCountDto(t.Name, live.Count(a => a.TagId == t.Id)))
            .Take(take + 1).ToListAsync(ct);
        return Results.Ok(Paging.ToPage(rows, take, t => t.Name));
    }

    private static async Task<IResult> DeleteAsync(
        string slug, string name, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        var tag = await db.Tags.SingleOrDefaultAsync(t => t.ProjectId == access.Project.Id && t.Name == name, ct)
                  ?? throw ApiException.NotFound("tag_not_found", "Tag not found");
        db.Tags.Remove(tag); // the database cascades to its assignments
        db.Audit(caller, access.Project.Id, "tag.delete", name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
