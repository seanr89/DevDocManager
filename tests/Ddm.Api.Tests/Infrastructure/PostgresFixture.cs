using Npgsql;
using Testcontainers.PostgreSql;

namespace Ddm.Api.Tests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"ddm_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<PostgresFixture>;
