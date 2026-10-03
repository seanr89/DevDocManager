using Ddm.Api.Projects;

namespace Ddm.Api.Tests.Infrastructure;

/// <summary>Each test gets its own database and its own app instance.</summary>
[Collection("db")]
public abstract class ApiTestBase(PostgresFixture pg) : IAsyncLifetime
{
    protected DdmApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync() => Factory = new DdmApiFactory(await pg.CreateDatabaseAsync());
    public async Task DisposeAsync() => await Factory.DisposeAsync();

    protected HttpClient Anonymous() => Factory.CreateClient();

    protected HttpClient ClientFor(string userId)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestAuth.TokenFor(userId));
        return client;
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>())!;

    protected static async Task<string> ProblemCodeAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    protected static async Task<ProjectDto> CreateProjectAsync(HttpClient client, string slug, string visibility = "private")
    {
        var r = await client.PostAsJsonAsync("/api/v1/projects", new { slug, name = slug, visibility });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await ReadAsync<ProjectDto>(r);
    }

    protected static async Task AddMemberAsync(HttpClient admin, string slug, string userId, string role)
    {
        var r = await admin.PutAsJsonAsync($"/api/v1/projects/{slug}/members/{Uri.EscapeDataString(userId)}", new { role });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
