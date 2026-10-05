using Ddm.Api.Common;
using Ddm.Api.Domain;

namespace Ddm.Api.Projects;

public sealed record ProjectDto(string Slug, string Name, string Description, string Visibility, string Role, DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags)
{
    public static ProjectDto From(Project p, Role role, IReadOnlyList<string> tags) =>
        new(p.Slug, p.Name, p.Description, Wire.Lower(p.Visibility), Wire.Lower(role), p.CreatedAt, tags);
}

public sealed record CreateProjectRequest(string? Slug, string? Name, string? Description, string? Visibility);
public sealed record UpdateProjectRequest(string? Name, string? Description, string? Visibility, string[]? Tags);
public sealed record MemberDto(string UserId, string Role);
public sealed record SetMemberRequest(string? Role);
public sealed record AuditEntryDto(long Id, string Actor, string Action, string Target, DateTimeOffset At);
