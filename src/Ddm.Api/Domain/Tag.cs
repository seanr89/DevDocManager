namespace Ddm.Api.Domain;

/// <summary>A project-scoped label, created on first use.</summary>
public class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
}
