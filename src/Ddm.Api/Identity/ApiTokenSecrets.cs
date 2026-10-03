using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Ddm.Api.Identity;

/// <summary>Token format: ddm_tok_{tokenId as 32 hex}_{base64url secret}. Only the SHA-256 of the secret is stored.</summary>
public static class ApiTokenSecrets
{
    public const string Prefix = "ddm_tok_";
    private const int IdLength = 32;

    public static (string Token, string Hash) Generate(Guid id)
    {
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return ($"{Prefix}{id:N}_{secret}", Hash(secret));
    }

    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    public static bool TryParse(string token, out Guid id, out string secret)
    {
        id = default;
        secret = "";
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = token.AsSpan(Prefix.Length);
        if (rest.Length <= IdLength + 1 || rest[IdLength] != '_') return false;
        if (!Guid.TryParseExact(rest[..IdLength], "N", out id)) return false;
        secret = rest[(IdLength + 1)..].ToString();
        return true;
    }
}
