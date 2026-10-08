namespace Ddm.Api.Tags;

public sealed record TagCountDto(string Name, int Count);
public sealed record SetTagsRequest(string[]? Tags);
public sealed record TagsDto(IReadOnlyList<string> Tags);
