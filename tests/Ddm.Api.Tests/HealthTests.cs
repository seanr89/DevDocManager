using Microsoft.AspNetCore.Hosting;

namespace Ddm.Api.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Healthz_returns_ok_without_credentials()
    {
        var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing")).CreateClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
