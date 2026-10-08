namespace Ddm.Api.Domain;

/// <summary>An OpenAPI definition. Its bodies live in versions; this row holds the parsed summary of the current one.</summary>
public class Spec
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public string Title { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string OpenApiVersion { get; set; } = "";
    /// <summary>"yaml" or "json": the format of the original upload.</summary>
    public string Format { get; set; } = "yaml";
    /// <summary>JSON array of {method, path, operationId, summary, tags} (jsonb).</summary>
    public string Operations { get; set; } = "[]";
    public int OperationCount { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public uint RowVersion { get; set; }
}
