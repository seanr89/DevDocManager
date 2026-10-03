using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Projects;

public static class ProjectEndpoints
{
    public static void MapProjects(this RouteGroupBuilder v1)
    {
        v1.MapPost("/projects", CreateAsync);
        v1.MapGet("/projects", ListAsync);
        v1.MapGet("/projects/{slug}", GetAsync);
        v1.MapPatch("/projects/{slug}", UpdateAsync);
        v1.MapDelete("/projects/{slug}", DeleteAsync);
    }

    private static async Task<IResult> CreateAsync(ClaimsPrincipal user, CreateProjectRequest? body, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        if (caller.Kind != CallerKind.User)
            throw ApiException.Forbidden("user_required", "API tokens cannot create projects");
        var v = ProjectValidation.ForCreate(body);

        var project = new Project { Slug = v.Slug, Name = v.Name, Description = v.Description, Visibility = v.Visibility };
        db.Projects.Add(project);
        db.Members.Add(new Member { ProjectId = project.Id, UserId = caller.UserId!, Role = Role.Admin });
        db.Audit(caller, project.Id, "project.create", project.Slug);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.Conflict("slug_taken", $"The project slug '{project.Slug}' is already taken");
        }
        return Results.Created($"/api/v1/projects/{project.Slug}", ProjectDto.From(project, Role.Admin));
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, string? cursor, int? limit, ProjectAuthorizer authz, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);

        var query = authz.VisibleTo(caller);
        if (after is not null) query = query.Where(p => string.Compare(p.Slug, after) > 0);
        var rows = await query.OrderBy(p => p.Slug).Take(take + 1).ToListAsync(ct);

        var page = Paging.ToPage(rows, take, p => p.Slug);
        var roles = await authz.RolesAsync(caller, [.. page.Items], ct);
        var dtos = page.Items.Select(p => ProjectDto.From(p, roles[p.Id])).ToList();
        return Results.Ok(new Page<ProjectDto>(dtos, page.Next));
    }

    private static async Task<IResult> GetAsync(string slug, ClaimsPrincipal user, ProjectAuthorizer authz, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        return Results.Ok(ProjectDto.From(access.Project, access.Role));
    }

    private static async Task<IResult> UpdateAsync(
        string slug, UpdateProjectRequest? body, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        if (body is null) throw ProjectValidation.Invalid("A JSON body is required");

        var p = access.Project;
        if (body.Name is not null) p.Name = ProjectValidation.Name(body.Name);
        if (body.Description is not null) p.Description = ProjectValidation.Description(body.Description);
        if (body.Visibility is not null) p.Visibility = ProjectValidation.ParseVisibility(body.Visibility);
        db.Audit(caller, p.Id, "project.update", p.Slug);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ProjectDto.From(p, access.Role));
    }

    private static async Task<IResult> DeleteAsync(
        string slug, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        var project = access.Project;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Versions have no FK (they are polymorphic), so remove them explicitly.
        await db.Versions
            .Where(v => v.ItemType == ItemType.Document
                        && db.Documents.Any(d => d.Id == v.ItemId && d.ProjectId == project.Id))
            .ExecuteDeleteAsync(ct);
        db.Projects.Remove(project); // cascades to members, documents, tokens
        db.Audit(caller, project.Id, "project.delete", project.Slug);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
