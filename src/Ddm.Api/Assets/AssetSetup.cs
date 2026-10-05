namespace Ddm.Api.Assets;

public static class AssetSetup
{
    public static IServiceCollection AddDdmAssets(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AssetOptions>(config.GetSection("Assets"));
        services.AddScoped<AssetService>();
        return services;
    }
}
