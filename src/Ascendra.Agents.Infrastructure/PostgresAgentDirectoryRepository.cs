using Npgsql;

namespace Ascendra.Agents.Infrastructure;

public sealed class PostgresAgentDirectoryRepository(PostgresDatabase database) : IAgentDirectoryRepository
{
    private const string Columns = "id, first_name, paternal_surname, maternal_surname, display_alias, username, role, is_active, revision";
    private const string ImportKey = "headcount-bootstrap-v1";

    public async Task<IReadOnlyList<AgentDirectoryRecord>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, cancellationToken);
    }

    public async Task<bool> SeedIfEmptyAsync(IReadOnlyList<AgentDirectoryRecord> directory, string fingerprint,
        CancellationToken cancellationToken = default)
    {
        var validated = AgentDirectoryRules.Validate(directory, requireActiveUsers: true);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        if ((await ReadAsync(connection, transaction, cancellationToken)).Count > 0) return false;
        await using (var marker = new NpgsqlCommand(
            "SELECT 1 FROM crm_agent_directory_imports WHERE import_key = @key", connection, transaction))
        {
            marker.Parameters.AddWithValue("key", ImportKey);
            if (await marker.ExecuteScalarAsync(cancellationToken) is not null)
                throw new InvalidOperationException("El padrón importado quedó vacío; recupéralo desde PostgreSQL antes de continuar.");
        }

        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var agent in validated)
        {
            var insert = new NpgsqlBatchCommand("""
                WITH inserted AS (
                    INSERT INTO crm_agent_directory
                        (id, first_name, paternal_surname, maternal_surname, display_alias, username, role, is_active)
                    VALUES (@id, @first, @paternal, @maternal, @alias, @username, @role, @active)
                    RETURNING *
                )
                INSERT INTO crm_agent_directory_history (agent_id, action, recorded_by, new_data)
                SELECT id, 'Importación inicial', 'bootstrap', to_jsonb(inserted) FROM inserted;
                """);
            AddParameters(insert.Parameters, agent);
            batch.BatchCommands.Add(insert);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await using (var record = new NpgsqlCommand("""
            INSERT INTO crm_agent_directory_imports (import_key, source_fingerprint, people_count)
            VALUES (@key, @fingerprint, @count);
            """, connection, transaction))
        {
            record.Parameters.AddWithValue("key", ImportKey);
            record.Parameters.AddWithValue("fingerprint", fingerprint);
            record.Parameters.AddWithValue("count", validated.Count);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<AgentDirectoryRecord> SaveAsync(AgentDirectoryRecord agent, string editorId,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        var current = await ReadAsync(connection, transaction, cancellationToken);
        var actor = current.FirstOrDefault(item => item.Id.Equals(editorId, StringComparison.OrdinalIgnoreCase));
        if (!AgentDirectoryRules.CanManage(actor))
            throw new InvalidOperationException("No tienes permiso para modificar el padrón.");
        var previous = current.FirstOrDefault(item => item.Id.Equals(agent.Id, StringComparison.OrdinalIgnoreCase));
        if (expectedRevision == 0)
        {
            if (previous is not null) throw new InvalidOperationException("El ID ya pertenece a una persona del padrón.");
            var created = agent with { Revision = 1 };
            created = AgentDirectoryRules.Validate(current.Append(created)).Single(item => item.Id == created.Id);
            await using var insert = new NpgsqlCommand("""
                WITH inserted AS (
                    INSERT INTO crm_agent_directory
                        (id, first_name, paternal_surname, maternal_surname, display_alias, username, role, is_active)
                    VALUES (@id, @first, @paternal, @maternal, @alias, @username, @role, @active)
                    RETURNING *
                )
                INSERT INTO crm_agent_directory_history (agent_id, action, recorded_by, new_data)
                SELECT id, 'Alta', @editor, to_jsonb(inserted) FROM inserted;
                """, connection, transaction);
            AddParameters(insert.Parameters, created);
            insert.Parameters.AddWithValue("editor", actor!.Id);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return created;
        }
        if (previous is null) throw new InvalidOperationException("La persona ya no existe en el padrón.");
        if (previous.Revision != expectedRevision)
            throw new InvalidOperationException("La persona cambió mientras la editabas. Recarga el padrón.");
        var proposed = agent with { Id = previous.Id, Revision = previous.Revision + 1 };
        var validated = AgentDirectoryRules.Validate(current.Select(item => item.Id == previous.Id ? proposed : item));
        proposed = validated.Single(item => item.Id == previous.Id);

        await using var command = new NpgsqlCommand("""
            WITH prior AS MATERIALIZED (
                SELECT * FROM crm_agent_directory WHERE id = @id AND revision = @revision FOR UPDATE
            ), updated AS (
                UPDATE crm_agent_directory AS directory
                SET first_name = @first, paternal_surname = @paternal, maternal_surname = @maternal,
                    display_alias = @alias, username = @username, role = @role, is_active = @active,
                    revision = directory.revision + 1, updated_at = now()
                FROM prior
                WHERE directory.id = prior.id
                RETURNING directory.*
            )
            INSERT INTO crm_agent_directory_history (agent_id, action, recorded_by, previous_data, new_data)
            SELECT updated.id, 'Modificación', @editor, to_jsonb(prior), to_jsonb(updated)
            FROM updated JOIN prior ON updated.id = prior.id;
            """, connection, transaction);
        AddParameters(command.Parameters, proposed);
        command.Parameters.AddWithValue("revision", expectedRevision);
        command.Parameters.AddWithValue("editor", actor!.Id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("La persona cambió mientras la editabas. Recarga el padrón.");
        await transaction.CommitAsync(cancellationToken);
        return proposed;
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(831731, 1);", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<AgentDirectoryRecord>> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var rows = new List<AgentDirectoryRecord>();
        var locking = transaction is null ? string.Empty : " FOR UPDATE";
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM crm_agent_directory ORDER BY id{locking};", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new AgentDirectoryRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7), reader.GetInt64(8)));
        return rows.AsReadOnly();
    }

    private static void AddParameters(NpgsqlParameterCollection parameters, AgentDirectoryRecord agent)
    {
        parameters.AddWithValue("id", agent.Id);
        parameters.AddWithValue("first", agent.FirstName);
        parameters.AddWithValue("paternal", agent.PaternalSurname);
        parameters.AddWithValue("maternal", agent.MaternalSurname);
        parameters.AddWithValue("alias", agent.DisplayAlias);
        parameters.AddWithValue("username", agent.Username);
        parameters.AddWithValue("role", agent.Role);
        parameters.AddWithValue("active", agent.IsActive);
    }
}
