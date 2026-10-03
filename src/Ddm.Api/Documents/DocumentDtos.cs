using System.Text.Json;
using Ddm.Api.Domain;

namespace Ddm.Api.Documents;

public sealed record DocumentDto(string Path, string Title, int Version, DateTimeOffset UpdatedAt, JsonElement FrontMatter)
{
    public static DocumentDto From(string path, ParsedMarkdown parsed, ContentVersion version)
    {
        using var doc = JsonDocument.Parse(parsed.FrontMatterJson);
        return new(path, parsed.Title, version.Number, version.CreatedAt, doc.RootElement.Clone());
    }
}

public sealed record CreateDocumentRequest(string? Path, string? Content, string? Message);
public sealed record DocumentSummaryDto(string Path, string Title, int Version, DateTimeOffset UpdatedAt);
public sealed record VersionDto(int Number, string Author, string? Message, DateTimeOffset CreatedAt);
