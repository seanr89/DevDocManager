using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Data;

public static class ProjectLocks
{
    /// <summary>Serialises writers on one project's membership. Call inside a transaction.</summary>
    public static Task<int> LockAsync(this DdmDbContext db, Guid projectId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Projects\" WHERE \"Id\" = {projectId} FOR UPDATE", ct);
}
