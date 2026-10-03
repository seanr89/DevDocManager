using Ddm.Api.Common;
using Ddm.Api.Documents;

namespace Ddm.Api.Tests;

public class DocumentPathTests
{
    [Theory]
    [InlineData("setup.md")] [InlineData("guides/setup.md")] [InlineData("a/b/c/d.md")]
    [InlineData("_drafts/x.md")] [InlineData("v1.2/notes-final_2.md")] [InlineData("a.md/b.md")]
    public void Valid_paths_are_accepted(string path) => Assert.Null(DocumentPath.Validate(path));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("setup")] [InlineData("setup.txt")] [InlineData("SETUP.MD")]
    [InlineData("/setup.md")] [InlineData("setup.md/")] [InlineData("a//b.md")] [InlineData("../x.md")]
    [InlineData("a/../x.md")] [InlineData("./x.md")] [InlineData(".hidden.md")] [InlineData("a/.md")]
    [InlineData("a\\b.md")] [InlineData("a b.md")] [InlineData("a%2fb.md")] [InlineData("é.md")]
    [InlineData("a\n/b.md")] [InlineData("a/b\n.md")] [InlineData("-x.md")]
    public void Invalid_paths_are_rejected(string? path) => Assert.NotNull(DocumentPath.Validate(path));

    [Fact] public void Overlong_paths_are_rejected() => Assert.NotNull(DocumentPath.Validate(new string('a', 253) + ".md"));
    [Fact] public void Overlong_segments_are_rejected() => Assert.NotNull(DocumentPath.Validate(new string('a', 101) + ".md"));
    [Fact] public void Too_many_segments_are_rejected() => Assert.NotNull(DocumentPath.Validate(string.Join('/', Enumerable.Repeat("a", 11)) + ".md"));

    [Fact]
    public void Require_throws_a_400_with_a_stable_code()
    {
        var ex = Assert.Throws<ApiException>(() => DocumentPath.Require("x.txt"));
        Assert.Equal(400, ex.Status);
        Assert.Equal("invalid_path", ex.Code);
    }
}
