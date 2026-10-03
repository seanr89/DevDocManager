using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ddm.Api.Tests.Infrastructure;

public static class TestAuth
{
    public const string SigningKey = "test-signing-key-test-signing-key-0123456789";
    public const string Issuer = "ddm-tests";
    public const string Audience = "ddm-api";

    public static string TokenFor(string userId, string? key = null, string? audience = null) =>
        Mint(userId, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), key, audience);

    public static string ExpiredTokenFor(string userId) =>
        Mint(userId, DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow.AddMinutes(-10), null, null);

    private static string Mint(string userId, DateTime notBefore, DateTime expires, string? key, string? audience) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience ?? Audience,
            Subject = new ClaimsIdentity([new Claim("sub", userId)]),
            NotBefore = notBefore,
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? SigningKey)), SecurityAlgorithms.HmacSha256),
        });
}
