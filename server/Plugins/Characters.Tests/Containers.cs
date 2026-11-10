using Cyberverse.Plugin.Characters.Data;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Cyberverse.Plugin.Characters.Tests;

/// <summary>
/// The database tests run against a real PostgreSQL container. Without a container engine they skip, unless
/// CYBERVERSE_REQUIRE_CONTAINERS=1 (set on the Linux CI runner), where that is a failure instead.
/// </summary>
public static class Containers
{
    public static bool Required => Environment.GetEnvironmentVariable("CYBERVERSE_REQUIRE_CONTAINERS") == "1";

    public static bool Available => Required || (OperatingSystem.IsLinux() &&
        (Environment.GetEnvironmentVariable("DOCKER_HOST") != null || File.Exists("/var/run/docker.sock")));
}

public sealed class ContainerFactAttribute : FactAttribute
{
    public ContainerFactAttribute()
    {
        if (!Containers.Available)
        {
            Skip = "Needs Linux containers (Docker or Podman); set CYBERVERSE_REQUIRE_CONTAINERS=1 to fail instead";
        }
    }
}

/// <summary>One PostgreSQL 18 container for the whole run; every test gets a fresh, migrated database in it.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        if (!Containers.Available)
        {
            return;
        }

        _container = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty database and migrates it; returns its connection string.</summary>
    public async Task<string> NewDatabaseAsync()
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        var admin = _container!.GetConnectionString();
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(admin + ";GSS Encryption Mode=Disable") { Database = name }
            .ConnectionString;
        await new CharacterStore(CharactersDbContext.Options(connectionString), () => DateTime.UtcNow).MigrateAsync();
        return connectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "database";
}
