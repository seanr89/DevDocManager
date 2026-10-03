namespace Ddm.Api.Domain;

public class Member
{
    public Guid ProjectId { get; set; }
    public required string UserId { get; set; }
    public Role Role { get; set; }
}
