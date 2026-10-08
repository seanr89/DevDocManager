using Ddm.Api.Assets;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Tests;

public class ContentUrlSignerTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string Sha = new('a', 64);

    private static (ContentUrlSigner Signer, FixedTime Time) Create(DateTimeOffset now)
    {
        var time = new FixedTime(now);
        var options = Options.Create(new ContentOptions
        {
            BaseUrl = "https://content.example", SigningKey = "unit-test-content-signing-key-0123456789",
        });
        return (new ContentUrlSigner(options, time), time);
    }

    [Fact]
    public void Urls_are_stable_within_a_day_and_change_across_days()
    {
        var day = new DateTimeOffset(2026, 10, 3, 0, 0, 1, TimeSpan.Zero);
        var morning = Create(day).Signer.UrlFor(Project, Sha);
        Assert.Equal(morning, Create(day.AddHours(23)).Signer.UrlFor(Project, Sha));
        Assert.NotEqual(morning, Create(day.AddDays(1)).Signer.UrlFor(Project, Sha));
        Assert.StartsWith($"https://content.example/content/{Project:N}/{Sha}?exp=", morning);
    }

    [Fact]
    public void Expiry_is_between_24_and_48_hours_away()
    {
        var now = new DateTimeOffset(2026, 10, 3, 13, 30, 0, TimeSpan.Zero);
        var exp = DateTimeOffset.FromUnixTimeSeconds(ContentUrlSigner.ExpiryAt(now));
        Assert.InRange(exp - now, TimeSpan.FromHours(24), TimeSpan.FromHours(48));
    }

    [Fact]
    public void A_valid_signature_verifies_until_it_expires()
    {
        var (signer, time) = Create(DateTimeOffset.UtcNow);
        var exp = ContentUrlSigner.ExpiryAt(time.Now);
        var sig = signer.Sign(Project, Sha, exp);
        Assert.Equal(SignatureCheck.Valid, signer.Verify(Project, Sha, exp, sig));
        time.Now = DateTimeOffset.FromUnixTimeSeconds(exp);
        Assert.Equal(SignatureCheck.Expired, signer.Verify(Project, Sha, exp, sig));
    }

    [Fact]
    public void Tampering_with_any_part_invalidates_the_signature()
    {
        var (signer, time) = Create(DateTimeOffset.UtcNow);
        var exp = ContentUrlSigner.ExpiryAt(time.Now);
        var sig = signer.Sign(Project, Sha, exp);
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Guid.NewGuid(), Sha, exp, sig));
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Project, new string('b', 64), exp, sig));
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Project, Sha, exp + 86_400, sig));
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Project, Sha, exp, sig[..^1] + (sig[^1] == 'A' ? 'B' : 'A')));
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Project, Sha, exp, null));
    }
}
