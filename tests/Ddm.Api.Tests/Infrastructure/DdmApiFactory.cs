using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Ddm.Api.Tests.Infrastructure;

public sealed class DdmApiFactory(string connectionString, bool migrate = true, string environment = "Testing")
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ddm"] = connectionString,
            ["Database:MigrateOnStart"] = migrate ? "true" : "false",
        }));
    }
}
