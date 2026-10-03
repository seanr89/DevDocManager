namespace Ddm.Api.Domain;

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Path { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Front matter as a JSON object string (jsonb column).</summary>
    public string FrontMatter { get; set; } = "{}";
    public Guid? CurrentVersionId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
