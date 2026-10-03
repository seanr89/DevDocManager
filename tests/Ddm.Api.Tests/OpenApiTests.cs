using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class OpenApiTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task The_api_publishes_its_own_openapi_document_anonymously()
    {
        var response = await Anonymous().GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("3.", doc.GetProperty("openapi").GetString());
        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/projects", out _));
        Assert.True(paths.TryGetProperty("/api/v1/projects/{slug}", out _));
        Assert.True(paths.TryGetProperty("/api/v1/me", out _));
    }
}
