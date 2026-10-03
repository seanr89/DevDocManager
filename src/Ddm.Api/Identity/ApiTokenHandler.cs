using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Ddm.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Identity;

public sealed class ApiTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, DdmDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";
    public const string HeaderPrefix = "Bearer " + ApiTokenSecrets.Prefix;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        if (!ApiTokenSecrets.TryParse(header["Bearer ".Length..].Trim(), out var id, out var secret))
            return AuthenticateResult.Fail("Malformed API token");

        var row = await db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, Context.RequestAborted);
        var hashOk = row is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(ApiTokenSecrets.Hash(secret)), Encoding.ASCII.GetBytes(row.HashedSecret));
        if (!hashOk || row!.RevokedAt is not null || (row.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow))
            return AuthenticateResult.Fail("Invalid API token");

        var identity = new ClaimsIdentity(
        [
            new Claim(Caller.TokenIdClaim, row.Id.ToString()),
            new Claim(Caller.TokenProjectClaim, row.ProjectId.ToString()),
            new Claim(Caller.TokenScopeClaim, row.Scope.ToString()),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
