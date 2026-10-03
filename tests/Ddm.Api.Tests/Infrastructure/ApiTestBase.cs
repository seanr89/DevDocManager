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
}
