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
}
