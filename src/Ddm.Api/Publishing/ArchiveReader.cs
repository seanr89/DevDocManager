using System.Formats.Tar;
using System.IO.Compression;
using Ddm.Api.Common;

namespace Ddm.Api.Publishing;

public enum ArchiveFormat { TarGz, Zip }

public sealed record ArchiveFile(string Path, byte[] Content);
public sealed record ArchiveContents(IReadOnlyList<ArchiveFile> Files, IReadOnlyList<string> Ignored);
public sealed record ArchiveLimits(long MaxExpandedBytes, int MaxEntries, long MaxEntryBytes);

/// <summary>A problem with one archive entry; any of these fails the whole publish.</summary>
public sealed record PublishError(string Path, string Code, string Message, int? Line = null);

/// <summary>
/// Reads a tar.gz or zip into memory, refusing anything that could write outside the project or exhaust the
/// server: links, absolute or '..' paths, too many entries, and more expanded bytes than allowed. Hidden entries
/// (any segment starting with '.') are skipped and reported, so a checkout's .git or .github never publishes.
/// </summary>
public static class ArchiveReader
{
    private enum EntryKind { File, Directory, Metadata, Unsupported }

    public static async Task<ArchiveContents> ReadAsync(Stream archive, ArchiveFormat format, ArchiveLimits limits, CancellationToken ct)
    {
        var state = new State(limits);
        try
        {
            if (format == ArchiveFormat.TarGz) await ReadTarAsync(archive, state, ct);
            else await ReadZipAsync(archive, state, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
        {
            throw ApiException.BadRequest("invalid_archive", "The archive could not be read", ex.Message);
        }
        if (state.Errors.Count > 0)
            throw ApiException.Unprocessable("publish_invalid", "The archive cannot be published", state.Errors,
                $"{state.Errors.Count} problem(s) found");
        return new(state.Files, state.Ignored);
    }

    private static async Task ReadTarAsync(Stream archive, State state, CancellationToken ct)
    {
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        await using var tar = new TarReader(gzip, leaveOpen: true);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            state.CountEntry();
            var kind = entry.EntryType switch
            {
                TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => EntryKind.File,
                TarEntryType.Directory => EntryKind.Directory,
                TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes => EntryKind.Metadata,
                _ => EntryKind.Unsupported,
            };
            if (kind == EntryKind.Metadata) continue;
            if (state.Accept(entry.Name, kind) is { } path) await state.AddAsync(path, entry.DataStream ?? Stream.Null, ct);
        }
    }

    private static async Task ReadZipAsync(Stream archive, State state, CancellationToken ct)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            state.CountEntry();
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000; // 0x8000 file, 0x4000 directory, 0xA000 symlink
            var kind = entry.FullName.EndsWith('/') ? EntryKind.Directory
                : unixType is 0 or 0x8000 ? EntryKind.File
                : EntryKind.Unsupported;
            if (state.Accept(entry.FullName, kind) is not { } path) continue;
            await using var data = entry.Open();
            await state.AddAsync(path, data, ct);
        }
    }

    private sealed class State(ArchiveLimits limits)
    {
        public readonly List<ArchiveFile> Files = [];
        public readonly List<string> Ignored = [];
        public readonly List<PublishError> Errors = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private long _expanded;
        private int _entries;

        public void CountEntry()
        {
            if (++_entries > limits.MaxEntries) throw TooLarge($"The archive has more than {limits.MaxEntries} entries");
        }

        /// <summary>Normalises an entry name. Returns the path to read, or null when the entry is skipped or refused (refusals are recorded).</summary>
        public string? Accept(string rawName, EntryKind kind)
        {
            var name = rawName.Replace('\\', '/');
            if (name.StartsWith('/') || (name.Length >= 2 && name[1] == ':'))
            {
                Errors.Add(new(rawName, "unsafe_path", "Absolute paths are not allowed in a publish archive"));
                return null;
            }
            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
            if (segments.Contains(".."))
            {
                Errors.Add(new(rawName, "unsafe_path", "'..' is not allowed in archive paths"));
                return null;
            }
            if (segments.Count == 0) return null; // the archive root, e.g. "./"
            var path = string.Join('/', segments);
            if (segments.Any(s => s.StartsWith('.')))
            {
                if (kind != EntryKind.Directory) Ignored.Add(path);
                return null;
            }
            switch (kind)
            {
                case EntryKind.Unsupported:
                    Errors.Add(new(path, "unsupported_entry", "Only regular files and directories can be published; links are not allowed"));
                    return null;
                case EntryKind.Directory:
                    return null;
            }
            if (!_seen.Add(path))
            {
                Errors.Add(new(path, "duplicate_entry", "The archive contains this path more than once"));
                return null;
            }
            return path;
        }

        public async Task AddAsync(string path, Stream data, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await data.ReadAsync(buffer, ct)) > 0)
            {
                _expanded += n;
                if (_expanded > limits.MaxExpandedBytes) throw TooLarge($"The archive expands to more than {limits.MaxExpandedBytes} bytes");
                if (ms.Length + n > limits.MaxEntryBytes) throw TooLarge($"{path} is larger than {limits.MaxEntryBytes} bytes");
                ms.Write(buffer, 0, n);
            }
            Files.Add(new(path, ms.ToArray()));
        }
    }

    private static ApiException TooLarge(string detail) => new(413, "archive_too_large", "The archive is too large", detail);
}
