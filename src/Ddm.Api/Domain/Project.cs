namespace Ddm.Api.Domain;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public Visibility Visibility { get; set; } = Visibility.Private;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
