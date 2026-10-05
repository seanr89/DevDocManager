using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Assets;

public static class ContentSetup
{
    public static IServiceCollection AddDdmContent(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ContentOptions>(config.GetSection("Content"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ContentUrlSigner>();
        return services;
    }

    /// <summary>Fail at startup, not on the first page render, if content links cannot be issued safely.</summary>
    public static void EnsureContentConfigured(this WebApplication app)
    {
        var o = app.Services.GetRequiredService<IOptions<ContentOptions>>().Value;
        if (o.SigningKey.Length < 32)
            throw new InvalidOperationException("Content:SigningKey must be set to at least 32 characters.");
        if (string.IsNullOrEmpty(o.BaseUrl) && !(app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")))
            throw new InvalidOperationException("Content:BaseUrl must name a separate origin for user content outside Development.");
    }
}
