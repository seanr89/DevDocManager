using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Data;

public static class DataSetup
{
    public static IServiceCollection AddDdmData(this IServiceCollection services)
    {
        services.AddDbContext<DdmDbContext>((sp, o) =>
            o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Ddm")));
        services.AddHealthChecks().AddDbContextCheck<DdmDbContext>("database", tags: ["ready"]);
        return services;
    }

    public static void MigrateIfConfigured(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStart")) return;
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DdmDbContext>().Database.Migrate();
    }

    public static void MapReadiness(this WebApplication app) =>
        app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
}
