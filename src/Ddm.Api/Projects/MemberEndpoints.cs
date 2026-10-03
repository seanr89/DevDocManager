using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Projects;

public static class MemberEndpoints
{
    public static void MapMembers(this RouteGroupBuilder v1)
    {
        v1.MapGet("/projects/{slug}/members", ListAsync);
        v1.MapPut("/projects/{slug}/members/{userId}", SetAsync);
        v1.MapDelete("/projects/{slug}/members/{userId}", RemoveAsync);
    }

    private static async Task<IResult> ListAsync(
        string slug, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Admin, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);

        var query = db.Members.Where(m => m.ProjectId == access.Project.Id);
        if (after is not null) query = query.Where(m => string.Compare(m.UserId, after) > 0);
        var rows = await query.OrderBy(m => m.UserId).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, m => m.UserId);
        return Results.Ok(new Page<MemberDto>(page.Items.Select(m => new MemberDto(m.UserId, Wire.Lower(m.Role))).ToList(), page.Next));
    }

    private static async Task<IResult> SetAsync(
        string slug, string userId, SetMemberRequest? body, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        ValidateUserId(userId);
        if (!Wire.TryParse<Role>(body?.Role, out var role))
            throw ProjectValidation.Invalid("role must be 'reader', 'editor' or 'admin'");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAsync(access.Project.Id, ct);
        var member = await db.Members.SingleOrDefaultAsync(m => m.ProjectId == access.Project.Id && m.UserId == userId, ct);
        if (member is null)
            db.Members.Add(member = new Member { ProjectId = access.Project.Id, UserId = userId, Role = role });
        else
        {
            if (member.Role == Role.Admin && role != Role.Admin) await EnsureAnotherAdminAsync(db, access.Project.Id, userId, ct);
            member.Role = role;
        }
        db.Audit(caller, access.Project.Id, "member.set", userId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new MemberDto(userId, Wire.Lower(role)));
    }

    private static async Task<IResult> RemoveAsync(
        string slug, string userId, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAsync(access.Project.Id, ct);
        var member = await db.Members.SingleOrDefaultAsync(m => m.ProjectId == access.Project.Id && m.UserId == userId, ct)
            ?? throw ApiException.NotFound("member_not_found", "Member not found");
        if (member.Role == Role.Admin) await EnsureAnotherAdminAsync(db, access.Project.Id, userId, ct);
        db.Members.Remove(member);
        db.Audit(caller, access.Project.Id, "member.remove", userId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task EnsureAnotherAdminAsync(DdmDbContext db, Guid projectId, string excludingUserId, CancellationToken ct)
    {
        var others = await db.Members.CountAsync(m => m.ProjectId == projectId && m.Role == Role.Admin && m.UserId != excludingUserId, ct);
        if (others == 0) throw ApiException.Conflict("last_admin", "A project must keep at least one admin");
    }

    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 256)
            throw ProjectValidation.Invalid("userId must be 1-256 characters");
    }
}
