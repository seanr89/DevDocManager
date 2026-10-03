using System.Security.Claims;

namespace Ddm.Api.Identity;

public sealed record MeDto(string Kind, string Actor, string? UserId, Guid? ProjectId, string? Scope);

public static class MeEndpoints
{
    public static void MapMe(this RouteGroupBuilder v1) => v1.MapGet("/me", (ClaimsPrincipal user) =>
    {
        var c = Caller.From(user);
        return Results.Ok(new MeDto(c.Kind.ToString().ToLowerInvariant(), c.Actor, c.UserId,
            c.TokenProjectId, c.TokenScope?.ToString().ToLowerInvariant()));
    });
}
