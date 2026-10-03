using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Domain;

namespace Ddm.Api.Identity;

public enum CallerKind { User, Token }

public sealed record Caller(
    CallerKind Kind, string Actor, string? UserId, Guid? TokenId, Guid? TokenProjectId, TokenScope? TokenScope)
{
    public const string TokenIdClaim = "ddm:token_id";
    public const string TokenProjectClaim = "ddm:project_id";
    public const string TokenScopeClaim = "ddm:scope";

    public static Caller From(ClaimsPrincipal p)
    {
        if (p.Identity?.IsAuthenticated != true)
            throw new ApiException(401, "unauthenticated", "Authentication required");

        if (p.FindFirstValue(TokenIdClaim) is { } tokenId)
            return new(CallerKind.Token, $"token:{tokenId}", null, Guid.Parse(tokenId),
                Guid.Parse(p.FindFirstValue(TokenProjectClaim)!),
                Enum.Parse<TokenScope>(p.FindFirstValue(TokenScopeClaim)!));

        var sub = p.FindFirstValue("sub") ?? throw new ApiException(401, "unauthenticated", "The token has no subject");
        return new(CallerKind.User, $"user:{sub}", sub, null, null, null);
    }
}
