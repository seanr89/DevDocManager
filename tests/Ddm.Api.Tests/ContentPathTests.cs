using Ddm.Api.Common;

namespace Ddm.Api.Tests;

public class ContentPathTests
{
    [Theory] [InlineData("a.png")] [InlineData("images/a.png")] [InlineData("_x/y-z.v2.txt")]
    public void Valid_paths(string path) => Assert.Null(ContentPath.ValidateSegments(path));

    [Theory]
    [InlineData("")] [InlineData("/a.png")] [InlineData("a//b.png")] [InlineData(".git/config")]
    [InlineData("a/../b.png")] [InlineData("a b.png")]
    public void Invalid_paths(string path) => Assert.NotNull(ContentPath.ValidateSegments(path));

    [Theory] [InlineData(null)] [InlineData("")] [InlineData("guides/")] [InlineData("guides/v2/")]
    public void Valid_folders(string? prefix) => Assert.Null(ContentPath.ValidateFolder(prefix));

    [Theory] [InlineData("guides")] [InlineData("/")] [InlineData("../")] [InlineData("a//")]
    public void Invalid_folders(string prefix) => Assert.NotNull(ContentPath.ValidateFolder(prefix));

    [Theory]
    [InlineData("guides/setup.md", "img/a.png", "guides/img/a.png")]
    [InlineData("guides/setup.md", "../images/a.png", "images/a.png")]
    [InlineData("guides/setup.md", "./a.png", "guides/a.png")]
    [InlineData("a/b/c.md", "../../x.png", "x.png")]
    [InlineData("index.md", "images/a%2Db.png", "images/a-b.png")]
    public void Resolves_relative_references(string from, string reference, string expected) =>
        Assert.Equal(expected, ContentPath.Resolve(from, reference));

    [Theory]
    [InlineData("index.md", "../escape.png")] [InlineData("a.md", "/abs.png")] [InlineData("a.md", "https://x/y.png")]
    [InlineData("a.md", "//host/y.png")] [InlineData("a.md", "a.png?x=1")] [InlineData("a.md", "a.png#top")]
    [InlineData("a.md", "data:image/png;base64,AA")] [InlineData("a.md", "")]
    public void Refuses_anything_that_is_not_a_plain_relative_path(string from, string reference) =>
        Assert.Null(ContentPath.Resolve(from, reference));
}
