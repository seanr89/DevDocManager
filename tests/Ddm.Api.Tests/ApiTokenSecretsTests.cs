using Ddm.Api.Identity;

namespace Ddm.Api.Tests;

public class ApiTokenSecretsTests
{
    [Fact]
    public void Generated_tokens_round_trip_through_parse()
    {
        var id = Guid.NewGuid();
        var (token, hash) = ApiTokenSecrets.Generate(id);
        Assert.StartsWith("ddm_tok_", token);
        Assert.True(ApiTokenSecrets.TryParse(token, out var parsedId, out var secret));
        Assert.Equal(id, parsedId);
        Assert.Equal(hash, ApiTokenSecrets.Hash(secret));
    }

    [Fact]
    public void The_hash_is_64_hex_chars_and_does_not_contain_the_secret()
    {
        var (token, hash) = ApiTokenSecrets.Generate(Guid.NewGuid());
        ApiTokenSecrets.TryParse(token, out _, out var secret);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.DoesNotContain(secret, hash);
    }

    [Fact]
    public void Two_tokens_differ() =>
        Assert.NotEqual(ApiTokenSecrets.Generate(Guid.NewGuid()).Token, ApiTokenSecrets.Generate(Guid.NewGuid()).Token);

    [Theory]
    [InlineData("")]
    [InlineData("ddm_tok_")]
    [InlineData("ddm_tok_notaguid_secret")]
    [InlineData("ddm_tok_00000000000000000000000000000000")]
    [InlineData("other_00000000000000000000000000000000_secret")]
    public void Malformed_tokens_do_not_parse(string token) =>
        Assert.False(ApiTokenSecrets.TryParse(token, out _, out _));
}
