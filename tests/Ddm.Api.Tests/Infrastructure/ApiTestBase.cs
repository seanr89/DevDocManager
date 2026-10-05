using System.Security.Cryptography;
using Ddm.Api.Data;
using Ddm.Api.Projects;
using Ddm.Api.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ddm.Api.Tests.Infrastructure;

/// <summary>Each test gets its own database and its own app instance.</summary>
[Collection("db")]
public abstract class ApiTestBase(PostgresFixture pg) : IAsyncLifetime
{
    private string _connectionString = null!;

    protected DdmApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await pg.CreateDatabaseAsync();
        Factory = new DdmApiFactory(_connectionString);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        // Npgsql pools outlive the app; without this, idle connections from every test database pile up
        // until Postgres refuses new clients (53300).
        using var conn = new NpgsqlConnection(_connectionString);
        NpgsqlConnection.ClearPool(conn);
    }

    /// <summary>Project ids are not part of the API surface, but signed content URLs are built from them.</summary>
    protected async Task<Guid> ProjectIdAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DdmDbContext>().Projects.Where(p => p.Slug == slug).Select(p => p.Id).SingleAsync();
    }

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

    protected static async Task<string> CreateTokenAsync(HttpClient admin, string slug, string scope, string name = "ci")
    {
        var r = await admin.PostAsJsonAsync($"/api/v1/projects/{slug}/tokens", new { name, scope });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
    }

    protected HttpClient TokenClient(string secret)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        return client;
    }

    protected static Task<HttpResponseMessage> PutDocAsync(
        HttpClient client, string slug, string path, string markdown, string? ifMatch = null, string? message = null, string? ifNoneMatch = null)
    {
        var url = $"/api/v1/projects/{slug}/docs/{path}" + (message is null ? "" : $"?message={Uri.EscapeDataString(message)}");
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(markdown, Encoding.UTF8, "text/markdown") };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return client.SendAsync(request);
    }

    protected static Task<HttpResponseMessage> GetDocAsync(HttpClient client, string slug, string path, string? accept = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/projects/{slug}/docs/{path}");
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
        return client.SendAsync(request);
    }

    /// <summary>The smallest bytes our sniffer accepts as PNG.</summary>
    protected static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    /// <summary>A different PNG, for replace tests.</summary>
    protected static byte[] PngVariant(byte n) => [.. PngBytes, n];

    protected static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    protected static Task<HttpResponseMessage> PutAssetAsync(
        HttpClient client, string slug, string path, byte[] bytes, string? ifMatch = null, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/projects/{slug}/assets/{path}") { Content = new ByteArrayContent(bytes) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return client.SendAsync(request);
    }

    protected static Task<HttpResponseMessage> PutSpecAsync(
        HttpClient client, string slug, string name, string content, string? ifMatch = null, string mediaType = "application/yaml")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/projects/{slug}/specs/{name}")
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType),
        };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request);
    }
}
