namespace Ddm.Api.Domain;

/// <summary>Immutable snapshot of a Document (or, later, a Spec). Table name: versions.</summary>
public class ContentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ItemType ItemType { get; set; }
    public Guid ItemId { get; set; }
    public int Number { get; set; }
    public required string ContentRef { get; set; }
    public required string ContentSha256 { get; set; }
    public required string Author { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
