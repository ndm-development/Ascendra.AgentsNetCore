using Npgsql;

namespace Ascendra.Agents.Tests;

public sealed class PostgresDirectoryTests
{
    [PostgresFact]
    public async Task ConcurrentInitializationAndSeedAreIdempotent()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var otherDatabase = test.NewDatabase();
        var other = new Ascendra.Agents.Infrastructure.PostgresAgentDirectoryRepository(otherDatabase);
        var result = await Task.WhenAll(test.Repository.SeedIfEmptyAsync(TestDatabase.Roster, "same-source"),
            other.SeedIfEmptyAsync(TestDatabase.Roster, "same-source"));
        Assert.Single(result, imported => imported);
        Assert.Equal(5, await test.CountAsync("crm_agent_directory"));
        Assert.Equal(5, await test.CountAsync("crm_agent_directory_history"));
        Assert.Equal(1, await test.CountAsync("crm_agent_directory_imports"));
        Assert.Equal(Ascendra.Agents.Infrastructure.DatabaseMigrations.All.Count, await test.CountAsync("application_schema_migrations"));
        var snapshot = await test.Service.GetAsync();
        Assert.Equal(5, snapshot.Directory.Count);
        Assert.Equal(4, snapshot.Users.Count);
    }

    [PostgresFact]
    public async Task UpdatesAreAuditedAndCannotBeOverwrittenByOldRevisionOrExcel()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "source");
        var before = await test.Service.GetAsync();
        var agent = before.Directory.Single(row => row.Id == "A");
        var saved = await test.Service.SaveAsync(agent with { IsActive = false, DisplayAlias = "Nuevo alias" }, "M", agent.Revision);
        Assert.Equal(2, saved.Revision);
        Assert.False(saved.IsActive);
        Assert.Equal(6, await test.CountAsync("crm_agent_directory_history"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.SaveAsync(agent, "M", agent.Revision));
        Assert.False((await test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "changed-source")).Imported);
        Assert.DoesNotContain((await test.Service.GetAsync()).Users, user => user.Id == "A");
        Assert.NotEqual(before.Version, (await test.Service.GetAsync()).Version);
        await using var connection = await test.Database.OpenConnectionAsync();
        await using var audit = new NpgsqlCommand("SELECT recorded_by, previous_data ->> 'is_active', new_data ->> 'is_active' FROM crm_agent_directory_history WHERE action='Modificación'", connection);
        await using var reader = await audit.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("M", reader.GetString(0)); Assert.Equal("true", reader.GetString(1)); Assert.Equal("false", reader.GetString(2));
    }

    [PostgresFact]
    public async Task PermissionsAndUserUniquenessAreCheckedInTransaction()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "source");
        var agent = (await test.Service.GetAsync()).Directory.Single(row => row.Id == "A");
        foreach (var editor in new[] { "A", "S", "I", "missing" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.SaveAsync(agent with { Role = "Gerente" }, editor, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.SaveAsync(agent with { Username = "M" }, "M", 1));
        Assert.Equal(5, await test.CountAsync("crm_agent_directory_history"));
        var saved = await test.Service.SaveAsync(agent with { DisplayAlias = "Alias de dirección" }, "D", 1);
        Assert.Equal(2, saved.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.GetForManagementAsync("S"));
    }

    [PostgresFact]
    public async Task CreateRetainsCanonicalNameAndRejectsIdCollision()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "source");
        var created = await test.Service.SaveAsync(DirectoryRulesTests.Person("N"), "M", 0);
        Assert.Equal(1, created.Revision);
        Assert.Equal("Nombre Compuesto Paterno Materno", created.DisplayName);
        Assert.Equal(6, await test.CountAsync("crm_agent_directory"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.SaveAsync(created with { Id = "n" }, "M", 0));
        Assert.Equal(6, await test.CountAsync("crm_agent_directory_history"));
    }

    [PostgresFact]
    public async Task InvalidInitialRosterLeavesNoPartialRowsOrImportMarker()
    {
        await using var test = await TestDatabase.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.SeedIfEmptyAsync(
            [DirectoryRulesTests.Person("A"), DirectoryRulesTests.Person("A")], "bad-source"));
        Assert.Equal(0, await test.CountAsync("crm_agent_directory"));
        Assert.Equal(0, await test.CountAsync("crm_agent_directory_imports"));
        await test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "valid-source");
        Assert.Equal(5, await test.CountAsync("crm_agent_directory"));
    }

    [PostgresFact]
    public async Task FailedAuditRollsBackThePersonChange()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "source");
        await using var connection = await test.Database.OpenConnectionAsync();
        await using var constraint = new NpgsqlCommand("ALTER TABLE crm_agent_directory_history ADD CONSTRAINT simulated_audit_failure CHECK (action <> 'Modificación')", connection);
        await constraint.ExecuteNonQueryAsync();
        var agent = (await test.Service.GetAsync()).Directory.Single(row => row.Id == "A");
        await Assert.ThrowsAsync<PostgresException>(() => test.Service.SaveAsync(agent with { IsActive = false }, "M", 1));
        var after = (await test.Service.GetAsync()).Directory.Single(row => row.Id == "A");
        Assert.Equal(agent, after);
        Assert.Equal(5, await test.CountAsync("crm_agent_directory_history"));
    }

    [PostgresFact]
    public async Task ConcurrentEditsAcceptOnlyOneRevisionAndOneAudit()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "source");
        await using var otherDatabase = test.NewDatabase();
        var other = new Ascendra.Agents.Application.AgentDirectoryService(new Ascendra.Agents.Infrastructure.PostgresAgentDirectoryRepository(otherDatabase));
        var agent = (await test.Service.GetAsync()).Directory.Single(row => row.Id == "A");
        async Task<bool> Save(Ascendra.Agents.Application.AgentDirectoryService service, string alias)
        {
            try { await service.SaveAsync(agent with { DisplayAlias = alias }, "M", 1); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var result = await Task.WhenAll(Save(test.Service, "Primera edición"), Save(other, "Segunda edición"));
        Assert.Single(result, accepted => accepted);
        Assert.Equal(6, await test.CountAsync("crm_agent_directory_history"));
        Assert.Equal(2, (await test.Service.GetAsync()).Directory.Single(row => row.Id == "A").Revision);
    }

    [PostgresFact]
    public async Task EmptyPreviouslyImportedDirectoryCannotResurrectFromExcel()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "source");
        await using var connection = await test.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("DELETE FROM crm_agent_directory", connection);
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "old-source"));
        Assert.Equal(0, await test.CountAsync("crm_agent_directory"));
        Assert.Equal(5, await test.CountAsync("crm_agent_directory_history"));
    }

    [PostgresFact]
    public async Task DirectoryMigrationPreservesExistingPromiseIdsAndDeletionHistory()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var connection = await test.Database.OpenConnectionAsync();
        // Represent an installed legacy schema with an existing promise and a persistent tombstone.
        await using (var prepare = new NpgsqlCommand("""
            DROP TABLE crm_agent_directory_history, crm_agent_directory_imports, crm_agent_directory;
            DELETE FROM application_schema_migrations WHERE migration_id='2026-11-27-01-crm-agent-directory';
            INSERT INTO promesas (id_promesa, cliente_unico, numero_cliente, fecha_promesa, numero_semanas,
                fecha_creacion, usu_alta, agente, origen, id_promesa_origen)
                VALUES (123, 'CLIENTE-PRUEBA', 'NUMERO-PRUEBA', DATE '2026-11-27', 0,
                    TIMESTAMPTZ '2026-11-27 12:00:00+00', 'I', 'Nombre histórico intacto', 'Historico', 'origen-123');
            INSERT INTO promesas_historial (id_promesa, accion, registrado_en, registrado_por, datos_anteriores)
                VALUES (456, 'Eliminación', now(), 'M', '{"id_promesa":456,"usu_alta":"I"}');
            """, connection)) await prepare.ExecuteNonQueryAsync();
        await using var upgraded = test.NewDatabase();
        await upgraded.StartAsync(default);
        await using var inspect = new NpgsqlCommand("""
            SELECT id_promesa, agente, usu_alta, id_promesa_origen FROM promesas;
            SELECT id_promesa, accion, datos_anteriores ->> 'usu_alta' FROM promesas_historial;
            """, connection);
        await using var reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(123, reader.GetInt32(0)); Assert.Equal("Nombre histórico intacto", reader.GetString(1));
        Assert.Equal("I", reader.GetString(2)); Assert.Equal("origen-123", reader.GetString(3));
        Assert.False(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(456, reader.GetInt32(0)); Assert.Equal("Eliminación", reader.GetString(1)); Assert.Equal("I", reader.GetString(2));
        Assert.False(await reader.ReadAsync());
        Assert.Equal(0, await test.CountAsync("crm_agent_directory"));
    }
}
