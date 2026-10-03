using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ddm.Api.Data;

/// <summary>Used only by `dotnet ef`; override the target with DDM_CONNECTION.</summary>
public sealed class DdmDbContextFactory : IDesignTimeDbContextFactory<DdmDbContext>
{
    public DdmDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DdmDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("DDM_CONNECTION")
                   ?? "Host=localhost;Port=5432;Database=ddm;Username=ddm;Password=ddm")
        .Options);
}
