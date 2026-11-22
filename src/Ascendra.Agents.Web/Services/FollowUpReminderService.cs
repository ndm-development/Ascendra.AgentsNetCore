using Ascendra.Agents.Web.Domain;
using Ascendra.Agents.Web.Infrastructure;
using Npgsql;

namespace Ascendra.Agents.Web.Services;

/// <summary>
/// Conserva los seguimientos programados y entrega cada recordatorio una sola
/// vez al agente que lo creó. La vista es responsable de presentar el aviso;
/// el servicio mantiene la fecha y la entrega consistentes en PostgreSQL.
/// </summary>
public sealed class FollowUpReminderService(
    PromiseRegistrationService promiseRegistrations,
    PostgresDatabase database)
{
    private readonly PromiseRegistrationService _promiseRegistrations = promiseRegistrations;
    private readonly PostgresDatabase _database = database;

    public async Task<FollowUpReminder> ScheduleAsync(
        FollowUpReminderRequest request,
        AgentUser agent,
        CancellationToken cancellationToken = default)
    {
        if (!agent.IsAgent)
        {
            throw new InvalidOperationException("Sólo los agentes pueden programar seguimientos.");
        }

        var lookup = await _promiseRegistrations.LookupClientAsync(request.ClientId, cancellationToken);
        if (!lookup.Exists)
        {
            throw new InvalidOperationException("El CLIENTE_UNICO no existe en la cartera vigente.");
        }

        var phone = TextNormalization.Phone(request.Phone);
        if (!lookup.ContactNumbers.Any(number => number.Number.Equals(phone, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Selecciona uno de los teléfonos asociados al cliente.");
        }

        // La captura es hora de Ciudad de México, pero PostgreSQL (timestamptz)
        // recibe instantes UTC. Npgsql rechaza DateTimeOffset con desfase local.
        var scheduledAt = OperationalClock.FromMexicoCityLocal(request.ScheduledAtLocal).ToUniversalTime();
        if (scheduledAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("Selecciona una fecha y hora futuras para el seguimiento.");
        }

        var note = (request.Note ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new InvalidOperationException("Agrega una nota para recordar el seguimiento.");
        }
        if (note.Length > 1000)
        {
            throw new InvalidOperationException("La nota puede tener hasta 1,000 caracteres.");
        }

        var entry = new FollowUpReminder(
            Guid.NewGuid(),
            agent.Id,
            agent.DisplayName,
            lookup.ClientId,
            lookup.ClientName,
            phone,
            scheduledAt,
            note,
            DateTimeOffset.UtcNow,
            null,
            null);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO recordatorios (
                id, agente_id, agente, cliente_unico, nombre_cliente,
                numero_telefono, fecha_recordatorio, nota, creado_en)
            VALUES (
                @id, @agentId, @agentName, @clientId, @clientName,
                @phone, @scheduledAt, @note, @createdAt);
            """, connection);
        AddParameters(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return entry;
    }

    public async Task<IReadOnlyList<FollowUpReminder>> GetUpcomingAsync(
        string? agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return [];

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, agente_id, agente, cliente_unico, nombre_cliente,
                   numero_telefono, fecha_recordatorio, nota, creado_en,
                   notificado_en, cancelado_en
            FROM recordatorios
            WHERE agente_id = @agentId
              AND cancelado_en IS NULL
              AND fecha_recordatorio >= @from
            ORDER BY fecha_recordatorio
            LIMIT 50;
            """, connection);
        command.Parameters.AddWithValue("agentId", agentId.Trim());
        command.Parameters.AddWithValue("from", DateTimeOffset.UtcNow.AddDays(-1));
        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task CancelAsync(
        Guid reminderId,
        AgentUser agent,
        CancellationToken cancellationToken = default)
    {
        if (!agent.IsAgent)
        {
            throw new InvalidOperationException("Sólo el agente que creó el seguimiento puede cancelarlo.");
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE recordatorios
            SET cancelado_en = @cancelledAt
            WHERE id = @id
              AND agente_id = @agentId
              AND cancelado_en IS NULL
            RETURNING id;
            """, connection);
        command.Parameters.AddWithValue("id", reminderId);
        command.Parameters.AddWithValue("agentId", agent.Id);
        command.Parameters.AddWithValue("cancelledAt", DateTimeOffset.UtcNow);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException("Este seguimiento ya no está disponible para cancelar.");
        }
    }

    /// <summary>
    /// Reclama los avisos vencidos en una transacción para que dos pestañas no
    /// notifiquen el mismo seguimiento más de una vez.
    /// </summary>
    public async Task<IReadOnlyList<FollowUpReminder>> ClaimDueAsync(
        string? agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return [];

        var notifiedAt = DateTimeOffset.UtcNow;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH pendientes AS (
                SELECT id
                FROM recordatorios
                WHERE agente_id = @agentId
                  AND fecha_recordatorio <= @notifiedAt
                  AND notificado_en IS NULL
                  AND cancelado_en IS NULL
                ORDER BY fecha_recordatorio
                FOR UPDATE SKIP LOCKED
            )
            UPDATE recordatorios AS recordatorio
            SET notificado_en = @notifiedAt
            FROM pendientes
            WHERE recordatorio.id = pendientes.id
            RETURNING recordatorio.id, recordatorio.agente_id, recordatorio.agente,
                      recordatorio.cliente_unico, recordatorio.nombre_cliente,
                      recordatorio.numero_telefono, recordatorio.fecha_recordatorio,
                      recordatorio.nota, recordatorio.creado_en,
                      recordatorio.notificado_en, recordatorio.cancelado_en;
            """, connection);
        command.Parameters.AddWithValue("agentId", agentId.Trim());
        command.Parameters.AddWithValue("notifiedAt", notifiedAt);
        var reminders = await ReadManyAsync(command, cancellationToken);
        return reminders.OrderBy(reminder => reminder.ScheduledAt).ToArray();
    }

    private static async Task<IReadOnlyList<FollowUpReminder>> ReadManyAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var reminders = new List<FollowUpReminder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) reminders.Add(Read(reader));
        return reminders;
    }

    private static FollowUpReminder Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));

    private static void AddParameters(NpgsqlCommand command, FollowUpReminder entry)
    {
        command.Parameters.AddWithValue("id", entry.Id);
        command.Parameters.AddWithValue("agentId", entry.AgentId);
        command.Parameters.AddWithValue("agentName", entry.AgentName);
        command.Parameters.AddWithValue("clientId", entry.ClientId);
        command.Parameters.AddWithValue("clientName", entry.ClientName);
        command.Parameters.AddWithValue("phone", entry.Phone);
        command.Parameters.AddWithValue("scheduledAt", entry.ScheduledAt);
        command.Parameters.AddWithValue("note", entry.Note);
        command.Parameters.AddWithValue("createdAt", entry.CreatedAt);
    }
}
