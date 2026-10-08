using Ddm.Api.Common;
using Ddm.Api.Documents;
using Microsoft.AspNetCore.Http;

namespace Ddm.Api.Tests;

public class PreconditionsTests
{
    private static WritePrecondition Parse(string? ifMatch = null, string? ifNoneMatch = null)
    {
        IHeaderDictionary h = new HeaderDictionary();
        if (ifMatch is not null) h.IfMatch = ifMatch;
        if (ifNoneMatch is not null) h.IfNoneMatch = ifNoneMatch;
        return Preconditions.Parse(h);
    }

    [Fact] public void No_headers_means_no_preconditions() => Assert.Equal(new WritePrecondition(null, false, false), Parse());
    [Fact] public void Version_etag() => Assert.Equal(7, Parse("\"v7\"").IfMatchVersion);
    [Fact] public void Star_matches_any() => Assert.True(Parse("*").IfMatchAny);
    [Fact] public void If_none_match_star() => Assert.True(Parse(ifNoneMatch: "*").IfNoneMatchAny);
    [Fact] public void Etag_formatting() => Assert.Equal("\"v12\"", Preconditions.ETag(12));

    [Theory]
    [InlineData("v7")] [InlineData("\"7\"")] [InlineData("\"v\"")] [InlineData("\"v0\"")] [InlineData("\"v-1\"")]
    [InlineData("\"vx\"")] [InlineData("W/\"v7\"")] [InlineData("\"v1\", \"v2\"")] [InlineData("\"v99999999999\"")]
    public void Malformed_if_match_is_a_400(string value)
    {
        var ex = Assert.Throws<ApiException>(() => Parse(value));
        Assert.Equal("invalid_etag", ex.Code);
    }

    [Fact]
    public void If_none_match_only_supports_star() =>
        Assert.Equal("invalid_etag", Assert.Throws<ApiException>(() => Parse(ifNoneMatch: "\"v1\"")).Code);

    private static readonly string Sha = new('a', 64);

    private static ShaPrecondition ParseSha(string? ifMatch = null, string? ifNoneMatch = null)
    {
        IHeaderDictionary h = new HeaderDictionary();
        if (ifMatch is not null) h.IfMatch = ifMatch;
        if (ifNoneMatch is not null) h.IfNoneMatch = ifNoneMatch;
        return Preconditions.ParseSha(h);
    }

    [Fact] public void Sha_etag() => Assert.Equal(Sha, ParseSha($"\"{Sha}\"").IfMatchSha);
    [Fact] public void Sha_star_matches_any() => Assert.True(ParseSha("*").IfMatchAny);
    [Fact] public void Sha_if_none_match_star() => Assert.True(ParseSha(ifNoneMatch: "*").IfNoneMatchAny);
    [Fact] public void Sha_etag_formatting() => Assert.Equal($"\"{Sha}\"", Preconditions.ShaETag(Sha));

    [Theory] [InlineData("\"v7\"")] [InlineData("\"abc\"")] [InlineData("abc")]
    public void A_version_or_short_etag_is_not_a_sha_etag(string value) =>
        Assert.Equal("invalid_etag", Assert.Throws<ApiException>(() => ParseSha(value)).Code);

    [Fact]
    public void Uppercase_sha_etags_are_rejected() =>
        Assert.Equal("invalid_etag", Assert.Throws<ApiException>(() => ParseSha($"\"{new string('A', 64)}\"")).Code);
}
