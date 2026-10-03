namespace Ddm.Api.Domain;

public class AuditEntry
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
