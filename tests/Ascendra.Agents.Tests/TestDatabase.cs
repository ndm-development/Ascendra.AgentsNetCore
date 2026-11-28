using Ascendra.Agents.Application;
using Ascendra.Agents.Domain;
using Ascendra.Agents.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ascendra.Agents.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASCENDRA_TEST_DATABASE")))
            Skip = "Configura ASCENDRA_TEST_DATABASE con PostgreSQL local para las pruebas de integración.";
    }
}

public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _admin;
    private readonly string _name;
    public string ConnectionString { get; }
    public PostgresDatabase Database { get; }
    public PostgresAgentDirectoryRepository Repository { get; }
    public AgentDirectoryService Service { get; }

    private TestDatabase(string admin, string name, string connectionString)
    {
        _admin = admin; _name = name; ConnectionString = connectionString;
        Database = NewDatabase();
        Repository = new(Database);
        Service = new(Repository);
    }

    public PostgresDatabase NewDatabase() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:Ascendra"] = ConnectionString }).Build(), NullLogger<PostgresDatabase>.Instance);

    public static async Task<TestDatabase> CreateAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ASCENDRA_TEST_DATABASE"));
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Las pruebas sólo permiten PostgreSQL local.");
        var admin = builder.ConnectionString;
        var name = $"ascendra_test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection)) await create.ExecuteNonQueryAsync();
        builder.Database = name;
        return new TestDatabase(admin, name, builder.ConnectionString);
    }

    public async Task<long> CountAsync(string table)
    {
        if (table is not ("crm_agent_directory" or "crm_agent_directory_history" or "crm_agent_directory_imports" or "application_schema_migrations"))
            throw new ArgumentException("Unknown test table.");
        await using var connection = await Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE {_name} WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    public static IReadOnlyList<AgentDirectoryRecord> Roster => [
        DirectoryRulesTests.Person("M", "Gerente"), DirectoryRulesTests.Person("D", "Director"),
        DirectoryRulesTests.Person("S", "Supervisor"), DirectoryRulesTests.Person("A"),
        DirectoryRulesTests.Person("I", active: false)];
}
