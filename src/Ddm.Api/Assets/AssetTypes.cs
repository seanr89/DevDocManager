using System.Text;
using System.Xml;

namespace Ddm.Api.Assets;

public sealed record AssetType(string Extension, string MediaType, bool IsImage, Func<byte[], bool> Sniff);

/// <summary>
/// The upload allow-list. The type comes from the extension and must be confirmed by the bytes;
/// the client's Content-Type is never trusted.
/// </summary>
public static class AssetTypes
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly AssetType[] All =
    [
        new(".png", "image/png", true, b => StartsWith(b, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])),
        new(".jpg", "image/jpeg", true, IsJpeg),
        new(".jpeg", "image/jpeg", true, IsJpeg),
        new(".gif", "image/gif", true, b => StartsWith(b, "GIF87a"u8) || StartsWith(b, "GIF89a"u8)),
        new(".webp", "image/webp", true, b => b.Length >= 12 && StartsWith(b, "RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8)),
        new(".ico", "image/x-icon", true, b => StartsWith(b, [0x00, 0x00, 0x01, 0x00])),
        new(".svg", "image/svg+xml", true, IsSvg),
        new(".pdf", "application/pdf", false, b => StartsWith(b, "%PDF-"u8)),
        new(".txt", "text/plain; charset=utf-8", false, IsText),
        new(".csv", "text/csv; charset=utf-8", false, IsText),
        new(".json", "application/json; charset=utf-8", false, IsText),
        new(".yaml", "application/yaml; charset=utf-8", false, IsText),
        new(".yml", "application/yaml; charset=utf-8", false, IsText),
    ];

    private static readonly Dictionary<string, AssetType> ByExtension = All.ToDictionary(t => t.Extension, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Extensions => All.Select(t => t.Extension);

    public static AssetType? ForPath(string path) => ByExtension.GetValueOrDefault(System.IO.Path.GetExtension(path));

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix) => bytes.AsSpan().StartsWith(prefix);

    private static bool IsJpeg(byte[] b) => StartsWith(b, [0xFF, 0xD8, 0xFF]);

    private static bool IsText(byte[] b)
    {
        if (b.AsSpan().Contains((byte)0)) return false;
        try
        {
            StrictUtf8.GetString(b);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>Well-formed XML whose root is &lt;svg&gt;. DTDs are refused, so entity expansion and external entities cannot run.</summary>
    private static bool IsSvg(byte[] b)
    {
        if (!IsText(b)) return false;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(b), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });
            var root = reader.MoveToContent() == XmlNodeType.Element ? reader.LocalName : null;
            while (reader.Read()) { }
            return root == "svg";
        }
        catch (XmlException) { return false; }
    }
}
