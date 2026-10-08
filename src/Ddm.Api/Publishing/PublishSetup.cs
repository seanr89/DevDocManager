namespace Ddm.Api.Publishing;

public static class PublishSetup
{
    public static IServiceCollection AddDdmPublishing(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PublishOptions>(config.GetSection("Publish"));
        services.AddScoped<PublishService>();
        return services;
    }
}
