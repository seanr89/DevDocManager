using Ddm.Api.Assets;
using Ddm.Api.Common;

namespace Ddm.Api.Tests;

public class AssetPathTests
{
    [Fact] public void A_valid_path_returns_its_type() => Assert.Equal("image/png", AssetPath.Require("images/a.png").MediaType);

    [Theory] [InlineData("a.md")] [InlineData("a//b.png")] [InlineData(".hidden.png")] [InlineData("")]
    public void Bad_paths_and_markdown_are_400(string path) =>
        Assert.Equal("invalid_path", Assert.Throws<ApiException>(() => AssetPath.Require(path)).Code);

    [Fact]
    public void Unknown_extensions_are_415()
    {
        var ex = Assert.Throws<ApiException>(() => AssetPath.Require("tool.exe"));
        Assert.Equal(415, ex.Status);
        Assert.Equal("unsupported_asset_type", ex.Code);
    }

    [Fact] public void IsValid_mirrors_Require() => Assert.True(AssetPath.IsValid("a/b.svg") && !AssetPath.IsValid("a/b.md"));

    [Fact] public void Plain_route() => Assert.Equal(new AssetRoute.Current("images/a.png"), AssetRoute.Parse("images/a.png"));
    [Fact] public void Tags_route() => Assert.Equal(new AssetRoute.Tags("images/a.png"), AssetRoute.Parse("images/a.png/tags"));
}
