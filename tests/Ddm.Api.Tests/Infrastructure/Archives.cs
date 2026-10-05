using System.Formats.Tar;
using System.IO.Compression;

namespace Ddm.Api.Tests.Infrastructure;

/// <summary>Builds publish archives in memory.</summary>
public static class Archives
{
    public static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    public static TarEntry File(string name, byte[] content) =>
        new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) };

    public static TarEntry Directory(string name) => new PaxTarEntry(TarEntryType.Directory, name);

    public static TarEntry Symlink(string name, string target) => new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = target };

    public static TarEntry HardLink(string name, string target) => new PaxTarEntry(TarEntryType.HardLink, name) { LinkName = target };

    /// <summary>A tar.gz of regular files.</summary>
    public static byte[] TarGz(params (string Name, byte[] Content)[] files) =>
        TarGzEntries(files.Select(f => File(f.Name, f.Content)).ToArray());

    public static byte[] TarGzEntries(params TarEntry[] entries)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var e in entries) tar.WriteEntry(e);
        return ms.ToArray();
    }

    public static byte[] Zip(params (string Name, byte[] Content)[] files) =>
        ZipEntries(files.Select(f => (f.Name, f.Content, 0)).ToArray());

    /// <summary>A zip whose entries may carry a Unix mode in their external attributes (0xA1FF marks a symlink).</summary>
    public static byte[] ZipEntries(params (string Name, byte[] Content, int UnixMode)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content, mode) in files)
            {
                var entry = zip.CreateEntry(name);
                if (mode != 0) entry.ExternalAttributes = mode << 16;
                using var s = entry.Open();
                s.Write(content);
            }
        return ms.ToArray();
    }
}
