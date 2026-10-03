using Ddm.Api.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ddm.Api.Tests.Infrastructure;

public sealed class DdmApiFactory(string connectionString, bool migrate = true, string environment = "Testing")
    : WebApplicationFactory<Program>
{
    public InMemoryBlobStore Blobs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ddm"] = connectionString,
            ["Database:MigrateOnStart"] = migrate ? "true" : "false",
            ["Auth:DevSigningKey"] = TestAuth.SigningKey,
            ["Auth:Issuer"] = TestAuth.Issuer,
            ["Auth:Audience"] = TestAuth.Audience,
        }));

        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IBlobStore>();
            s.AddSingleton<IBlobStore>(Blobs);
        });
    }
}
