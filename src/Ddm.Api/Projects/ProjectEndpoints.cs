using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Tags;
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
        return Results.Created($"/api/v1/projects/{project.Slug}", ProjectDto.From(project, Role.Admin, []));
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, string? cursor, int? limit, string[]? tag, ProjectAuthorizer authz, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);
        var filter = TagName.Filter(tag);

        // Filtering narrows what VisibleTo allows; it can never widen it.
        var query = authz.VisibleTo(caller);
        if (filter.Count > 0)
        {
            var tagged = tags.ItemsWithAll(ItemType.Project, filter);
            query = query.Where(p => tagged.Contains(p.Id));
        }
        if (after is not null) query = query.Where(p => string.Compare(p.Slug, after) > 0);
        var rows = await query.OrderBy(p => p.Slug).Take(take + 1).ToListAsync(ct);

        var page = Paging.ToPage(rows, take, p => p.Slug);
        var roles = await authz.RolesAsync(caller, [.. page.Items], ct);
        var tagMap = await tags.TagsForAsync(ItemType.Project, page.Items.Select(p => p.Id).ToList(), ct);
        var dtos = page.Items.Select(p => ProjectDto.From(p, roles[p.Id], tagMap[p.Id])).ToList();
        return Results.Ok(new Page<ProjectDto>(dtos, page.Next));
    }

    private static async Task<IResult> GetAsync(
        string slug, ClaimsPrincipal user, ProjectAuthorizer authz, TagService tags, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        return Results.Ok(ProjectDto.From(access.Project, access.Role,
            await tags.TagsForAsync(new ItemRef(ItemType.Project, access.Project.Id), ct)));
    }

    private static async Task<IResult> UpdateAsync(
        string slug, UpdateProjectRequest? body, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db,
        TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        if (body is null) throw ProjectValidation.Invalid("A JSON body is required");

        var p = access.Project;
        var names = body.Tags is null ? null : TagName.NormalizeSet(body.Tags);
        if (body.Name is not null) p.Name = ProjectValidation.Name(body.Name);
        if (body.Description is not null) p.Description = ProjectValidation.Description(body.Description);
        if (body.Visibility is not null) p.Visibility = ProjectValidation.ParseVisibility(body.Visibility);
        if (names is not null) await tags.StageSetAsync(p.Id, new ItemRef(ItemType.Project, p.Id), names, ct);
        db.Audit(caller, p.Id, "project.update", p.Slug);
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex) when (names is not null && TagService.IsTagRace(ex)) { throw TagService.TagsConflict(); }
        return Results.Ok(ProjectDto.From(p, access.Role, await tags.TagsForAsync(new ItemRef(ItemType.Project, p.Id), ct)));
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
            .Where(v => (v.ItemType == ItemType.Document && db.Documents.Any(d => d.Id == v.ItemId && d.ProjectId == project.Id))
                        || (v.ItemType == ItemType.Spec && db.Specs.Any(s => s.Id == v.ItemId && s.ProjectId == project.Id)))
            .ExecuteDeleteAsync(ct);
        db.Projects.Remove(project); // cascades to members, documents, assets, specs, tags (and their assignments), tokens
        db.Audit(caller, project.Id, "project.delete", project.Slug);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
