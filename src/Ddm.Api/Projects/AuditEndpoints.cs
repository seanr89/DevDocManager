using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Projects;

public static class AuditEndpoints
{
    public static void MapAudit(this RouteGroupBuilder v1) => v1.MapGet("/projects/{slug}/audit", ListAsync);

    private static async Task<IResult> ListAsync(
        string slug, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Admin, ct);
        var take = Paging.ParseLimit(limit);
        var before = Paging.DecodeLongCursor(cursor);

        var query = db.AuditEntries.Where(e => e.ProjectId == access.Project.Id);
        if (before is not null) query = query.Where(e => e.Id < before);
        var rows = await query.OrderByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, e => e.Id.ToString());
        var dtos = page.Items.Select(e => new AuditEntryDto(e.Id, e.Actor, e.Action, e.Target, e.At)).ToList();
        return Results.Ok(new Page<AuditEntryDto>(dtos, page.Next));
    }
}
