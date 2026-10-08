namespace Ddm.Api.Specs;

public static class SpecSetup
{
    public static IServiceCollection AddDdmSpecs(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SpecOptions>(config.GetSection("Specs"));
        services.AddScoped<SpecService>();
        return services;
    }
}
