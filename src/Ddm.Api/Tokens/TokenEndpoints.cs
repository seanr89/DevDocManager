using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Projects;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Tokens;

public sealed record CreateTokenRequest(string? Name, string? Scope, DateTimeOffset? ExpiresAt);

public sealed record TokenDto(Guid Id, string Name, string Scope, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt)
{
    public static TokenDto From(ApiToken t) =>
        new(t.Id, t.Name, Wire.Lower(t.Scope), t.CreatedBy, t.CreatedAt, t.ExpiresAt, t.RevokedAt);
}

/// <summary>Returned once, at creation. The secret is never retrievable again.</summary>
public sealed record CreatedTokenDto(Guid Id, string Name, string Scope, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string Secret);

public static class TokenEndpoints
{
    public static void MapTokens(this RouteGroupBuilder v1)
    {
        v1.MapGet("/projects/{slug}/tokens", ListAsync);
        v1.MapPost("/projects/{slug}/tokens", CreateAsync);
        v1.MapDelete("/projects/{slug}/tokens/{id:guid}", RevokeAsync);
    }

    private static async Task<IResult> ListAsync(
        string slug, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Admin, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);

        var query = db.ApiTokens.Where(t => t.ProjectId == access.Project.Id);
        if (after is not null && Guid.TryParse(after, out var afterId)) query = query.Where(t => t.Id.CompareTo(afterId) > 0);
        else if (after is not null) throw ApiException.BadRequest("invalid_cursor", "The cursor is not valid");
        var rows = await query.OrderBy(t => t.Id).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, t => t.Id.ToString());
        return Results.Ok(new Page<TokenDto>(page.Items.Select(TokenDto.From).ToList(), page.Next));
    }

    private static async Task<IResult> CreateAsync(
        string slug, CreateTokenRequest? body, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        var name = body?.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100) throw ProjectValidation.Invalid("name must be 1-100 characters");
        if (!Wire.TryParse<TokenScope>(body?.Scope, out var scope)) throw ProjectValidation.Invalid("scope must be 'read' or 'write'");
        if (body!.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow) throw ProjectValidation.Invalid("expiresAt must be in the future");

        var id = Guid.NewGuid();
        var (token, hash) = ApiTokenSecrets.Generate(id);
        var row = new ApiToken
        {
            Id = id, ProjectId = access.Project.Id, Name = name, Scope = scope, HashedSecret = hash,
            CreatedBy = caller.Actor, ExpiresAt = body.ExpiresAt,
        };
        db.ApiTokens.Add(row);
        db.Audit(caller, access.Project.Id, "token.create", $"{id} ({name})");
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/projects/{slug}/tokens", new CreatedTokenDto(id, name, Wire.Lower(scope), row.CreatedAt, row.ExpiresAt, token));
    }

    private static async Task<IResult> RevokeAsync(
        string slug, Guid id, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        var row = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == id && t.ProjectId == access.Project.Id, ct)
            ?? throw ApiException.NotFound("token_not_found", "Token not found");
        if (row.RevokedAt is null)
        {
            row.RevokedAt = DateTimeOffset.UtcNow;
            db.Audit(caller, access.Project.Id, "token.revoke", id.ToString());
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }
}
