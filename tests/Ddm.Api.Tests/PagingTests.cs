using Ddm.Api.Common;

namespace Ddm.Api.Tests;

public class PagingTests
{
    [Fact] public void Limit_defaults_to_50() => Assert.Equal(50, Paging.ParseLimit(null));
    [Fact] public void Limit_is_clamped_to_200() => Assert.Equal(200, Paging.ParseLimit(10_000));
    [Theory] [InlineData(0)] [InlineData(-5)]
    public void Non_positive_limits_are_rejected(int limit)
    {
        var ex = Assert.Throws<ApiException>(() => Paging.ParseLimit(limit));
        Assert.Equal("invalid_limit", ex.Code);
    }

    [Fact] public void Cursor_round_trips() => Assert.Equal("my-slug", Paging.DecodeCursor(Paging.EncodeCursor("my-slug")));
    [Fact] public void Missing_cursor_means_first_page() => Assert.Null(Paging.DecodeCursor(null));

    [Theory] [InlineData("!!!not-base64!!!")] [InlineData("a")]
    public void Garbage_cursors_are_rejected(string cursor)
    {
        var ex = Assert.Throws<ApiException>(() => Paging.DecodeCursor(cursor));
        Assert.Equal("invalid_cursor", ex.Code);
    }

    [Fact]
    public void Non_numeric_long_cursor_is_rejected() =>
        Assert.Equal("invalid_cursor", Assert.Throws<ApiException>(() => Paging.DecodeLongCursor(Paging.EncodeCursor("abc"))).Code);

    [Fact]
    public void ToPage_trims_the_extra_row_and_sets_next()
    {
        var page = Paging.ToPage(["a", "b", "c"], 2, s => s);
        Assert.Equal(["a", "b"], page.Items);
        Assert.Equal("b", Paging.DecodeCursor(page.Next));
    }

    [Fact]
    public void ToPage_has_no_next_on_the_last_page() =>
        Assert.Null(Paging.ToPage(["a", "b"], 2, s => s).Next);
}
