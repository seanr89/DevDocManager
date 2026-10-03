using Microsoft.AspNetCore.Hosting;

namespace Ddm.Api.Tests;

public class ProblemDetailsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Unknown_routes_return_problem_json_with_a_stable_code()
    {
        var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing")).CreateClient();
        var response = await client.GetAsync("/api/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Equal(404, body.GetProperty("status").GetInt32());
    }
}
