using System.Formats.Tar;
using Ddm.Api.Common;
using Ddm.Api.Publishing;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class ArchiveReaderTests
{
    private static readonly ArchiveLimits Limits = new(MaxExpandedBytes: 1_000_000, MaxEntries: 100, MaxEntryBytes: 500_000);

    private static Task<ArchiveContents> ReadAsync(byte[] archive, ArchiveFormat format = ArchiveFormat.TarGz, ArchiveLimits? limits = null) =>
        ArchiveReader.ReadAsync(new MemoryStream(archive), format, limits ?? Limits, default);

    private static async Task<(string Code, List<string> EntryCodes)> FailAsync(
        byte[] archive, ArchiveFormat format = ArchiveFormat.TarGz, ArchiveLimits? limits = null)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => ReadAsync(archive, format, limits));
        var entries = (ex.Extensions?["errors"] as IEnumerable<PublishError>)?.Select(e => e.Code).ToList() ?? [];
        return (ex.Code, entries);
    }

    [Fact]
    public async Task Reads_files_and_skips_directories()
    {
        var c = await ReadAsync(Archives.TarGzEntries(Archives.Directory("guides/"), Archives.File("guides/a.md", Archives.Text("# a")), Archives.File("b.png", [1, 2])));
        Assert.Equal(["guides/a.md", "b.png"], c.Files.Select(f => f.Path));
        Assert.Equal("# a", Encoding.UTF8.GetString(c.Files[0].Content));
    }

    // Review Focus 3: what `tar czf docs.tgz .` produces on macOS
    [Fact]
    public async Task Dot_slash_prefixes_are_stripped_and_hidden_entries_ignored()
    {
        var c = await ReadAsync(Archives.TarGzEntries(
            Archives.Directory("./"), Archives.File("./index.md", Archives.Text("# i")), Archives.File("./._index.md", [0]),
            Archives.File("./.git/config", Archives.Text("x")), Archives.File("./.DS_Store", [0]), Archives.File("./empty.txt", [])));
        Assert.Equal(["index.md", "empty.txt"], c.Files.Select(f => f.Path));
        Assert.Empty(c.Files[1].Content);
        Assert.Equal(["._index.md", ".git/config", ".DS_Store"], c.Ignored);
    }

    // A real `tar czf docs.tgz .` from macOS bsdtar 3.5.3 (docs/ holding index.md, guides/a.md, empty.txt, .DS_Store,
    // .git/config; index.md and a.md carried xattrs, so bsdtar emitted ._ AppleDouble companions). Unlike the
    // hand-built archive above, its root entry is "." and directories have no trailing slash.
    private const string MacTarball = "H4sIAPT2w2oAA+2YTW/aMBjH003TNu7brpZ6N36NYRUHtm5qpaJOpZrWE/LAsGhNaIPRwj7JDpO2jzozKNBACUw4TIp/kpXE+A0/+ueJ/7DsWQchJDgHf6/+5Gq4u04eMCc+EoJSnwGEKeLCA9z+0jxvONAyNksZKBnFsttVsR6tavfti1LXa8a5/6eAncXuHlhW4Y0eQZ1oa3OY/fAZ2yb+TDDmAWRtRQsUPv7wuNlq6n6srM2RHX+cjj+n2MU/D5J9L8CxV4z+W0HUUQkMO7bmyNI/Ycvvf+L0nw9PXj71HnleQ7bBeRN8AlPGdd5zU4gpkSnj59+bDVm/vLyY3o57/DDlWarJwbz+RbsfQnlzc63g7VDGMtJBpMw2VuhRcrQyFI7dAcsfZHKiZEfFZVvvgUz9L+V/n3HigVxyU8H1TxEIdRCqGhZVTIjwqQ9xteKzKuG0xCk4O31Tv3h7cvrxHUyk1jFcJdda47jOGt81O6dfS8wHTdPh7Gpdhzt9l/a9AQUHWlP9nOzvf7b0/Y+Iy/95cAgCJ8ECA8u9YdBRA5s2INra/2OUOv8vF8z5rxdouybw9vGngiAX/zyYxr/dj7pBz9Ic2fmfpOLPMUIu/+dB22X/QjPL/7AlbR0CMs//VKT1T4nTfz5s4v+F3sT/+7XZkAv+37jHT1Mep5oczOtfzf2BUGnZkVq+7vb75sfPJjAOu8z0P7cBd/4eyNI/Fmn/TyCOnf+XBw/4f4IIgnGJVdb5f4tyrV2F70clRh+y/u61NcJ2nx3/BzP9W8v+/+L/cSSoy/95cAikk6LD4XAUkD87OGBGACwAAA==";

    [Fact]
    public async Task A_real_macos_tar_publishes_cleanly()
    {
        var c = await ReadAsync(Convert.FromBase64String(MacTarball));
        Assert.Equal(["empty.txt", "index.md", "guides/a.md"], c.Files.Select(f => f.Path));
        Assert.Empty(c.Files[0].Content);
        Assert.Equal("# i\n", Encoding.UTF8.GetString(c.Files[1].Content));
        Assert.Equal([".DS_Store", "._index.md", ".git/config", "guides/._a.md"], c.Ignored);
    }

    [Fact]
    public async Task Zip_archives_read_the_same_way()
    {
        var c = await ReadAsync(Archives.Zip(("a.md", Archives.Text("# a")), ("img/b.png", [1])), ArchiveFormat.Zip);
        Assert.Equal(["a.md", "img/b.png"], c.Files.Select(f => f.Path));
    }

    // Spec review focus 3: archive hostility
    [Theory]
    [InlineData("../evil.md")] [InlineData("a/../../evil.md")] [InlineData("/etc/passwd.md")] [InlineData("C:/x.md")]
    public async Task Unsafe_paths_fail_the_whole_archive(string name)
    {
        var (code, entries) = await FailAsync(Archives.TarGzEntries(Archives.File("ok.md", Archives.Text("# ok")), Archives.File(name, Archives.Text("x"))));
        Assert.Equal("publish_invalid", code);
        Assert.Equal(["unsafe_path"], entries);
    }

    [Fact]
    public async Task Symlinks_and_hard_links_fail_the_archive()
    {
        var (code, entries) = await FailAsync(Archives.TarGzEntries(Archives.Symlink("a.md", "/etc/passwd"), Archives.HardLink("b.md", "a.md")));
        Assert.Equal("publish_invalid", code);
        Assert.Equal(["unsupported_entry", "unsupported_entry"], entries);
    }

    [Fact]
    public async Task Zip_symlinks_fail_the_archive()
    {
        var (code, entries) = await FailAsync(Archives.ZipEntries(("link.md", Archives.Text("/etc/passwd"), 0xA1FF)), ArchiveFormat.Zip);
        Assert.Equal("publish_invalid", code);
        Assert.Equal(["unsupported_entry"], entries);
    }

    [Fact]
    public async Task Duplicate_paths_fail_the_archive()
    {
        var (_, entries) = await FailAsync(Archives.TarGzEntries(Archives.File("a.md", Archives.Text("1")), Archives.File("./a.md", Archives.Text("2"))));
        Assert.Equal(["duplicate_entry"], entries);
    }

    [Fact]
    public async Task Too_many_entries_is_413()
    {
        var files = Enumerable.Range(0, 11).Select(i => Archives.File($"f{i}.md", Archives.Text("x"))).ToArray();
        var (code, _) = await FailAsync(Archives.TarGzEntries(files), limits: Limits with { MaxEntries = 10 });
        Assert.Equal("archive_too_large", code);
    }

    [Fact]
    public async Task A_zip_bomb_stops_at_the_expanded_limit()
    {
        var zeros = new byte[600_000]; // compresses to almost nothing
        var (code, _) = await FailAsync(Archives.TarGz(("a.txt", zeros), ("b.txt", zeros)), limits: Limits with { MaxEntryBytes = 1_000_000 });
        Assert.Equal("archive_too_large", code);
    }

    [Fact]
    public async Task An_oversized_entry_is_413()
    {
        var (code, _) = await FailAsync(Archives.TarGz(("big.txt", new byte[500_001])));
        Assert.Equal("archive_too_large", code);
    }

    [Theory] [InlineData(ArchiveFormat.TarGz)] [InlineData(ArchiveFormat.Zip)]
    public async Task Corrupt_archives_are_400(ArchiveFormat format)
    {
        var (code, _) = await FailAsync([1, 2, 3, 4, 5], format);
        Assert.Equal("invalid_archive", code);
    }
}
