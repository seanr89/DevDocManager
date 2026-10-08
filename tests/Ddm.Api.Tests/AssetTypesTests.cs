using Ddm.Api.Assets;

namespace Ddm.Api.Tests;

public class AssetTypesTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static AssetType Type(string path) => AssetTypes.ForPath(path)!;

    [Theory]
    [InlineData("a.png", "image/png")] [InlineData("A.PNG", "image/png")] [InlineData("x/y.jpeg", "image/jpeg")]
    [InlineData("d.svg", "image/svg+xml")] [InlineData("r.yml", "application/yaml; charset=utf-8")]
    public void Types_come_from_the_extension(string path, string media) => Assert.Equal(media, Type(path).MediaType);

    [Theory] [InlineData("a.exe")] [InlineData("a.html")] [InlineData("a.zip")] [InlineData("noext")] [InlineData("a.md")]
    public void Other_extensions_are_not_allowed(string path) => Assert.Null(AssetTypes.ForPath(path));

    [Fact] public void Png_bytes_match_png() => Assert.True(Type("a.png").Sniff(Png));
    [Fact] public void Png_bytes_do_not_match_jpeg() => Assert.False(Type("a.jpg").Sniff(Png));
    [Fact] public void A_png_renamed_svg_is_not_svg() => Assert.False(Type("a.svg").Sniff(Png));

    [Fact]
    public void Svg_with_a_script_is_still_svg() =>
        Assert.True(Type("a.svg").Sniff(Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")));

    [Fact]
    public void Svg_with_a_dtd_or_external_entity_is_refused() =>
        Assert.False(Type("a.svg").Sniff(Utf8(
            "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg>&x;</svg>")));

    [Fact] public void Html_is_not_svg() => Assert.False(Type("a.svg").Sniff(Utf8("<html><body/></html>")));

    [Fact]
    public void Text_must_be_utf8_without_nul()
    {
        var txt = Type("a.txt");
        Assert.True(txt.Sniff(Utf8("héllo")));
        Assert.False(txt.Sniff([0x68, 0x00, 0x69]));
        Assert.False(txt.Sniff([0xC3, 0x28]));
    }

    [Fact]
    public void Gif_webp_ico_and_pdf_signatures()
    {
        Assert.True(Type("a.gif").Sniff(Utf8("GIF89a....")));
        Assert.True(Type("a.webp").Sniff(Utf8("RIFF\0\0\0\0WEBPVP8 ")));
        Assert.True(Type("a.ico").Sniff([0, 0, 1, 0, 1, 0]));
        Assert.True(Type("a.pdf").Sniff(Utf8("%PDF-1.7\n")));
        Assert.False(Type("a.pdf").Sniff(Utf8("<html>")));
    }
}
