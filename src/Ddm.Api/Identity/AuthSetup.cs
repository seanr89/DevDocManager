using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Ddm.Api.Identity;

public static class AuthSetup
{
    public static IServiceCollection AddDdmAuthentication(this IServiceCollection services)
    {
        const string Smart = "Smart";
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = Smart;
                o.DefaultChallengeScheme = Smart;
            })
            .AddPolicyScheme(Smart, "Bearer JWT or API token", o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.Authorization.ToString().StartsWith(ApiTokenHandler.HeaderPrefix, StringComparison.OrdinalIgnoreCase)
                    ? ApiTokenHandler.SchemeName
                    : JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(ApiTokenHandler.SchemeName, _ => { });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration, IHostEnvironment>((o, config, env) =>
            {
                o.MapInboundClaims = false;
                var audience = config["Auth:Audience"];
                var authority = config["Auth:Authority"];
                if (!string.IsNullOrEmpty(authority))
                {
                    o.Authority = authority;
                    o.Audience = audience;
                    return;
                }

                var key = config["Auth:DevSigningKey"];
                if (string.IsNullOrEmpty(key) || env.IsProduction())
                    throw new InvalidOperationException(
                        "Configure Auth:Authority (OIDC). Auth:DevSigningKey is only allowed outside Production.");

                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = config["Auth:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(5),
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>Fail at startup, not on the first request, if authentication is misconfigured.</summary>
    public static void EnsureAuthConfigured(this WebApplication app) =>
        app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
}
