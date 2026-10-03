using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Identity;

public sealed record ProjectAccess(Project Project, Role Role);

/// <summary>The single answer to "can this caller do this on this project". Every handler calls it.</summary>
public sealed class ProjectAuthorizer(DdmDbContext db)
{
    /// <summary>404 if the caller cannot even read the project, 403 if they can read but lack <paramref name="needed"/>.</summary>
    public async Task<ProjectAccess> RequireAsync(Caller caller, string slug, Role needed, CancellationToken ct)
    {
        var project = await db.Projects.SingleOrDefaultAsync(p => p.Slug == slug, ct);
        Role? role = null;
        if (project is not null && (await RolesAsync(caller, [project], ct)).TryGetValue(project.Id, out var r)) role = r;

        if (project is null || role is null)
            throw ApiException.NotFound("project_not_found", "Project not found");
        if (role < needed)
            throw ApiException.Forbidden("insufficient_role", $"This action needs the {Wire.Lower(needed)} role");
        return new(project, role.Value);
    }

    public async Task<Dictionary<Guid, Role>> RolesAsync(Caller caller, IReadOnlyCollection<Project> projects, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Role>();
        if (caller.Kind == CallerKind.Token)
        {
            foreach (var p in projects.Where(p => p.Id == caller.TokenProjectId))
                result[p.Id] = caller.TokenScope == TokenScope.Write ? Role.Editor : Role.Reader;
            return result;
        }

        var ids = projects.Select(p => p.Id).ToList();
        var memberRoles = await db.Members
            .Where(m => m.UserId == caller.UserId && ids.Contains(m.ProjectId))
            .ToDictionaryAsync(m => m.ProjectId, m => m.Role, ct);
        foreach (var p in projects)
        {
            if (memberRoles.TryGetValue(p.Id, out var role)) result[p.Id] = role;
            else if (p.Visibility == Visibility.Internal) result[p.Id] = Role.Reader;
        }
        return result;
    }

    public IQueryable<Project> VisibleTo(Caller caller) => caller.Kind == CallerKind.Token
        ? db.Projects.Where(p => p.Id == caller.TokenProjectId)
        : db.Projects.Where(p => p.Visibility == Visibility.Internal
                                 || db.Members.Any(m => m.ProjectId == p.Id && m.UserId == caller.UserId));
}
