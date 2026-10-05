namespace Ddm.Api.Domain;

/// <summary>An image or other binary. Not versioned: replacing it points the row at new content-addressed bytes.</summary>
public class Asset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Path { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Sha256 { get; set; }
    public required string StorageKey { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string UpdatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public uint RowVersion { get; set; }
}
