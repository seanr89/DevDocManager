using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Assets;

public enum SignatureCheck { Valid, Invalid, Expired }

/// <summary>
/// Signs /content links so browsers can load images without a bearer token. Expiry is bucketed to 24-hour UTC
/// windows (the end of today plus one day), so a link is stable, and so cacheable, for a day and lives at most 48 hours.
/// </summary>
public sealed class ContentUrlSigner(IOptions<ContentOptions> options, TimeProvider time)
{
    private const long Day = 86_400;

    public string UrlFor(Guid projectId, string sha256)
    {
        var exp = ExpiryAt(time.GetUtcNow());
        return $"{options.Value.BaseUrl.TrimEnd('/')}/content/{projectId:N}/{sha256}?exp={exp}&sig={Sign(projectId, sha256, exp)}";
    }

    public static long ExpiryAt(DateTimeOffset now) => (now.ToUnixTimeSeconds() / Day + 2) * Day;

    public string Sign(Guid projectId, string sha256, long exp)
    {
        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Value.SigningKey), Encoding.UTF8.GetBytes($"{projectId:N}/{sha256}/{exp}"));
        return Base64Url.EncodeToString(mac);
    }

    public SignatureCheck Verify(Guid projectId, string sha256, long exp, string? sig)
    {
        if (string.IsNullOrEmpty(sig)) return SignatureCheck.Invalid;
        var expected = Encoding.UTF8.GetBytes(Sign(projectId, sha256, exp));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(sig))) return SignatureCheck.Invalid;
        return time.GetUtcNow().ToUnixTimeSeconds() >= exp ? SignatureCheck.Expired : SignatureCheck.Valid;
    }
}
