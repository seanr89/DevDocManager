using Ddm.Api.Domain;
using Ddm.Api.Identity;

namespace Ddm.Api.Data;

public static class AuditExtensions
{
    /// <summary>Stages an audit entry; it is committed by the caller's SaveChanges, atomically with the change.</summary>
    public static void Audit(this DdmDbContext db, Caller caller, Guid projectId, string action, string target) =>
        db.AuditEntries.Add(new AuditEntry { ProjectId = projectId, Actor = caller.Actor, Action = action, Target = target });
}
