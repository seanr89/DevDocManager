using Ddm.Api.Domain;

namespace Ddm.Api.Specs;

public sealed record SpecDto(string Name, string Title, string ApiVersion, string OpenApiVersion, string Format, int Version,
    int OperationCount, IReadOnlyList<string> Tags, DateTimeOffset UpdatedAt)
{
    public static SpecDto From(Spec s, int version, IReadOnlyList<string> tags) =>
        new(s.Name, s.Title, s.ApiVersion, s.OpenApiVersion, s.Format, version, s.OperationCount, tags, s.UpdatedAt);
}

public sealed record CreateSpecRequest(string? Name, string? Content, string? Message);
