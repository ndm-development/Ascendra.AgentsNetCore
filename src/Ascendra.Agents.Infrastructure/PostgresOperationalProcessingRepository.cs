using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Ascendra.Agents.Infrastructure;

/// <summary>
/// Adaptador PostgreSQL del proceso operativo. Lee exclusivamente las vistas
/// vigentes de importación y publica un corte completo en tablas separadas;
/// nunca modifica los lotes crudos.
/// </summary>
public sealed class PostgresOperationalProcessingRepository(PostgresDatabase database) : IOperationalProcessingRepository
{
    private const string RuleVersion = "2026-11-27-csharp-v1";

    public async Task<OperationalProcessingInput> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var payments = (await RawRowsAsync(connection, "crm_pagos_vigentes", cancellationToken)).Select(row => new OperationalPayment(
            row.Id, Text(row.Data, "cliente_unico"), Date(row.Data, "fecha_recepcion"), Decimal(row.Data, "recuperacion_por_gestion"),
            Integer(row.Data, "semana"), Text(row.Data, "producto"), Text(row.Data, "concepto_calculo"),
            Text(row.Data, "plan_de_pago"), Text(row.Data, "zona_geografica"))).ToArray();
        var portfolio = (await RawRowsAsync(connection, "crm_cartera_vigentes", cancellationToken)).Select(row => new OperationalPortfolio(
            Text(row.Data, "cliente_unico"), Text(row.Data, "nombre_cte", "nombre_cliente", "nombre"),
            Text(row.Data, "producto"), Decimal(row.Data, "saldo_total", "saldo"), Text(row.Data, "estatus"),
            Text(row.Data, "fidiapago", "dia_de_pago"), Text(row.Data, "zona_geografica"),
            [Text(row.Data, "telefono1", "telefono_1"), Text(row.Data, "telefono2", "telefono_2"),
             Text(row.Data, "telefono3", "telefono_3"), Text(row.Data, "telefono4", "telefono_4")])).ToArray();
        var references = (await RawRowsAsync(connection, "crm_referencias_vigentes", cancellationToken)).Select(row => new OperationalReference(
            Text(row.Data, "cliente_unico"), Text(row.Data, "telefono_1", "telefono1", "telefono"),
            Text(row.Data, "referencia", "nombre_referencia"), Text(row.Data, "parentesco"))).ToArray();
        var seeds = (await RawRowsIfExistsAsync(connection, "crm_referencias_acumuladas_vigentes", cancellationToken))
            .Select(row => new OperationalContactSeed(Text(row.Data, "cliente_unico"), Text(row.Data, "telefono_1", "telefono1", "telefono"),
                Text(row.Data, "referencia", "nombre_referencia"), Text(row.Data, "parentesco"), "Referencia"))
            .Concat((await RawRowsIfExistsAsync(connection, "crm_contactos_vigentes", cancellationToken))
                .Select(row => new OperationalContactSeed(Text(row.Data, "cliente_unico"), Text(row.Data, "telefono", "telefono_1", "telefono1"),
                    Text(row.Data, "nombre_referencia", "referencia"), Text(row.Data, "parentesco"), Text(row.Data, "origen", "source"))))
            .ToArray();
        var geography = (await RawRowsIfExistsAsync(connection, "crm_geografia_maestro_vigentes", cancellationToken))
            .Select(row => new OperationalGeography(Text(row.Data, "cliente_unico"), Text(row.Data, "zona_geografica", "zona"))).ToArray();
        var agents = (await RawRowsAsync(connection, "crm_catalogo_agentes_vigentes", cancellationToken)).Select(row => new OperationalAgent(
            Text(row.Data, "agente_id", "id_agente", "id"), Text(row.Data, "agente", "nombre"))).ToArray();
        var promises = await PromisesAsync(connection, cancellationToken);
        return new(await SourceFingerprintAsync(connection, cancellationToken), payments, promises, portfolio, references, seeds, geography, agents);
    }

    public async Task<OperationalProcessingRun?> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT run.id_ejecucion, run.huella_fuentes, run.terminado_en
            FROM crm_proceso_actual current
            JOIN crm_proceso_ejecuciones run ON run.id_ejecucion=current.id_ejecucion
            WHERE current.id=true;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var id = reader.GetGuid(0); var fingerprint = reader.GetString(1); var completed = reader.GetFieldValue<DateTimeOffset>(2);
        await reader.CloseAsync();
        return new OperationalProcessingRun(id, fingerprint, completed,
            new(await PortfolioAsync(connection, id, cancellationToken), await ContactsAsync(connection, id, cancellationToken), await PaymentsAsync(connection, id, cancellationToken),
                await ProcessedPromisesAsync(connection, id, cancellationToken)));
    }

    public async Task<OperationalProcessingRun> SaveAsync(string sourceFingerprint, OperationalProcessingResult result,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "SET LOCAL lock_timeout = '5s'; SELECT pg_advisory_xact_lock(831731, 3);", cancellationToken);

        await using (var existing = new NpgsqlCommand("""
            SELECT run.id_ejecucion, run.terminado_en FROM crm_proceso_ejecuciones run
            JOIN crm_proceso_actual current ON current.id_ejecucion=run.id_ejecucion
            WHERE run.huella_fuentes=@fingerprint AND current.id=true;
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("fingerprint", sourceFingerprint);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var existingId = reader.GetGuid(0); var completed = reader.GetFieldValue<DateTimeOffset>(1);
                await reader.CloseAsync();
                await transaction.CommitAsync(cancellationToken);
                return new OperationalProcessingRun(existingId, sourceFingerprint, completed, result);
            }
        }

        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO crm_proceso_ejecuciones (id_ejecucion, huella_fuentes, version_reglas)
            VALUES (@id, @fingerprint, @rules) RETURNING terminado_en;
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("fingerprint", sourceFingerprint);
            insert.Parameters.AddWithValue("rules", RuleVersion);
            var completed = (DateTimeOffset)(await insert.ExecuteScalarAsync(cancellationToken))!;
            await CopyPortfolioAsync(connection, id, result.Portfolio, cancellationToken);
            await CopyContactsAsync(connection, id, result.Contacts, cancellationToken);
            await CopyPaymentsAsync(connection, id, result.Payments, cancellationToken);
            await CopyPromisesAsync(connection, id, result.Promises, cancellationToken);
            await PublishClientAndContactHistoryAsync(connection, transaction, id, cancellationToken);
            await AuditAndApplyStatusesAsync(connection, transaction, id, cancellationToken);
            await using var publish = new NpgsqlCommand("""
                INSERT INTO crm_proceso_actual (id, id_ejecucion, actualizado_en)
                VALUES (true, @id, now())
                ON CONFLICT (id) DO UPDATE SET id_ejecucion=excluded.id_ejecucion, actualizado_en=excluded.actualizado_en;
                """, connection, transaction);
            publish.Parameters.AddWithValue("id", id);
            await publish.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new OperationalProcessingRun(id, sourceFingerprint, completed, result);
        }
    }

    private static async Task<IReadOnlyList<OperationalPromise>> PromisesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id_promesa,
                   CASE WHEN lower(coalesce(origen, 'Ascendra')) <> 'ascendra'
                             AND nullif(btrim(id_promesa_origen), '') IS NOT NULL
                        THEN btrim(id_promesa_origen)
                        ELSE 'ASC-' || lpad(id_promesa::text, 6, '0') END,
                   CASE WHEN lower(coalesce(origen, 'Ascendra')) = 'ascendra' THEN id_promesa ELSE NULL END,
                   cliente_unico, numero_cliente, agente, usu_alta, fecha_promesa,
                   monto_inicial, monto_semanal, tipo_promesa, numero_semanas,
                   fecha_creacion AT TIME ZONE 'America/Mexico_City'
            FROM promesas ORDER BY fecha_creacion, id_promesa;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<OperationalPromise>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new(reader.GetInt32(0), reader.GetString(1), NullableInt(reader, 2), NullableText(reader, 3),
                NullableText(reader, 4), NullableText(reader, 5), NullableText(reader, 6), NullableDate(reader, 7),
                NullableDecimal(reader, 8), NullableDecimal(reader, 9), NullableText(reader, 10), NullableInt(reader, 11),
                reader.IsDBNull(12) ? null : reader.GetDateTime(12)));
        }
        return rows;
    }

    private static async Task<string> SourceFingerprintAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT md5(concat_ws('|',
                coalesce((SELECT string_agg(conjunto || ':' || clave_origen || ':' || sha256, '|' ORDER BY conjunto, clave_origen)
                          FROM crm_import_lotes
                          WHERE vigente IS TRUE AND conjunto IN ('pagos', 'cartera', 'referencias', 'referencias_acumuladas',
                              'contactos', 'catalogo_agentes', 'geografia_maestro')), ''),
                coalesce((SELECT md5(coalesce(string_agg(md5((to_jsonb(p) - 'estatus')::text), '' ORDER BY id_promesa), ''))
                          FROM promesas p), '')));
            """, connection);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<IReadOnlyList<RawRow>> RawRowsAsync(NpgsqlConnection connection, string view, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT _id, to_jsonb(row)::text FROM {view} row ORDER BY _id;", connection);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<RawRow>();
            while (await reader.ReadAsync(cancellationToken))
            {
                using var document = JsonDocument.Parse(reader.GetString(1));
                rows.Add(new(reader.GetInt64(0), document.RootElement.Clone()));
            }
            return rows;
        }
        catch (PostgresException exception) when (exception.SqlState == "42P01")
        {
            throw new InvalidOperationException($"Falta la vista {view}; carga y publica primero su fuente operativa.", exception);
        }
    }

    private static async Task<IReadOnlyList<RawRow>> RawRowsIfExistsAsync(NpgsqlConnection connection, string view, CancellationToken cancellationToken)
    {
        try { return await RawRowsAsync(connection, view, cancellationToken); }
        catch (InvalidOperationException exception) when (exception.InnerException is PostgresException { SqlState: "42P01" }) { return []; }
    }

    private static async Task<IReadOnlyList<ProcessedContact>> ContactsAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT contacto.cliente_unico, contacto.telefono, contacto.nombre_referencia, contacto.parentesco,
                   CASE WHEN cliente.activo THEN COALESCE(cartera.nombre_cliente, '') ELSE '' END,
                   CASE WHEN cliente.activo THEN COALESCE(cartera.producto, '') ELSE '' END,
                   CASE WHEN cliente.activo THEN cartera.saldo_total ELSE NULL END,
                   CASE WHEN cliente.activo THEN 'Activo' ELSE 'Inactivo' END, contacto.origen
            FROM crm_contactos_operativos contacto
            JOIN crm_clientes_operativos cliente ON cliente.cliente_unico=contacto.cliente_unico
            LEFT JOIN LATERAL (
                SELECT nombre_cliente, producto, saldo_total FROM crm_proceso_cartera
                WHERE id_ejecucion=cliente.id_ejecucion_actual AND cliente_unico=contacto.cliente_unico
                ORDER BY posicion LIMIT 1
            ) cartera ON true
            ORDER BY contacto.cliente_unico, contacto.telefono, contacto.origen, contacto.nombre_referencia, contacto.parentesco;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ProcessedContact>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new(NullableText(reader, 0) ?? string.Empty,
            NullableText(reader, 1) ?? string.Empty, NullableText(reader, 2) ?? string.Empty, NullableText(reader, 3) ?? string.Empty,
            NullableText(reader, 4) ?? string.Empty, NullableText(reader, 5) ?? string.Empty, NullableDecimal(reader, 6),
            NullableText(reader, 7) ?? string.Empty, NullableText(reader, 8) ?? string.Empty));
        return rows;
    }

    private static async Task<IReadOnlyList<ProcessedPortfolio>> PortfolioAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT cliente_unico, nombre_cliente, producto, saldo_total, estatus, dia_pago, zona_geografica,
                   telefonos::text FROM crm_proceso_cartera WHERE id_ejecucion=@id ORDER BY posicion;
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ProcessedPortfolio>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var phones = reader.IsDBNull(7) ? Array.Empty<string>() : JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? Array.Empty<string>();
            rows.Add(new(NullableText(reader, 0) ?? string.Empty, NullableText(reader, 1) ?? string.Empty,
                NullableText(reader, 2) ?? string.Empty, NullableDecimal(reader, 3), NullableText(reader, 4) ?? string.Empty,
                NullableText(reader, 5) ?? string.Empty, NullableText(reader, 6) ?? string.Empty, phones));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<ProcessedPayment>> PaymentsAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id_pago_origen, cliente_unico, fecha_recepcion, recuperacion_por_gestion, semana,
                   producto, concepto_calculo, plan_de_pago, zona_geografica, agente, agente_id,
                   nombre_cliente, dia_semana, id_promesa_asignada, id_promesa_base, tipo_asignacion
            FROM crm_proceso_pagos WHERE id_ejecucion=@id ORDER BY id_pago_origen;
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ProcessedPayment>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new(reader.GetInt64(0), NullableText(reader, 1) ?? string.Empty,
            NullableDate(reader, 2), NullableDecimal(reader, 3), NullableInt(reader, 4), NullableText(reader, 5) ?? string.Empty,
            NullableText(reader, 6) ?? string.Empty, NullableText(reader, 7) ?? string.Empty, NullableText(reader, 8) ?? string.Empty,
            NullableText(reader, 9) ?? string.Empty, NullableText(reader, 10) ?? string.Empty, NullableText(reader, 11) ?? string.Empty,
            NullableText(reader, 12) ?? string.Empty, NullableText(reader, 13) ?? string.Empty, NullableInt(reader, 14),
            NullableText(reader, 15) ?? string.Empty));
        return rows;
    }

    private static async Task<IReadOnlyList<ProcessedPromise>> ProcessedPromisesAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id_promesa_base, id_promesa, id_promesa_ascendra, cliente_unico, nombre_cliente,
                   numero_cliente, agente, agente_id, fecha_promesa, monto_inicial, monto_semanal,
                   tipo_promesa, numero_semanas, fecha_creacion, estatus
            FROM crm_proceso_promesas WHERE id_ejecucion=@id ORDER BY id_promesa_base;
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ProcessedPromise>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new(reader.GetInt32(0), NullableText(reader, 1) ?? string.Empty,
            NullableInt(reader, 2), NullableText(reader, 3) ?? string.Empty, NullableText(reader, 4) ?? string.Empty,
            NullableText(reader, 5) ?? string.Empty, NullableText(reader, 6) ?? string.Empty, NullableText(reader, 7) ?? string.Empty,
            NullableDate(reader, 8), NullableDecimal(reader, 9), NullableDecimal(reader, 10), NullableText(reader, 11) ?? string.Empty,
            NullableInt(reader, 12), reader.IsDBNull(13) ? null : reader.GetDateTime(13), NullableText(reader, 14) ?? string.Empty));
        return rows;
    }

    private static async Task CopyContactsAsync(NpgsqlConnection connection, Guid id, IReadOnlyList<ProcessedContact> rows, CancellationToken cancellationToken)
    {
        await using var copy = await connection.BeginBinaryImportAsync("""
            COPY crm_proceso_contactos (id_ejecucion, posicion, cliente_unico, telefono, nombre_referencia, parentesco,
                nombre_cliente, producto, saldo_total, estatus, origen) FROM STDIN (FORMAT BINARY)
            """, cancellationToken);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index]; await copy.StartRowAsync(cancellationToken);
            await copy.WriteAsync(id, NpgsqlDbType.Uuid, cancellationToken); await copy.WriteAsync(index, NpgsqlDbType.Integer, cancellationToken);
            await WriteNullableAsync(copy, row.ClientId, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.Phone, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.ReferenceName, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.Relationship, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.ClientName, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.Product, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.TotalBalance, NpgsqlDbType.Numeric, cancellationToken); await WriteNullableAsync(copy, row.Status, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.Source, NpgsqlDbType.Text, cancellationToken);
        }
        await copy.CompleteAsync(cancellationToken);
    }

    private static async Task CopyPortfolioAsync(NpgsqlConnection connection, Guid id, IReadOnlyList<ProcessedPortfolio> rows, CancellationToken cancellationToken)
    {
        await using var copy = await connection.BeginBinaryImportAsync("""
            COPY crm_proceso_cartera (id_ejecucion, posicion, cliente_unico, nombre_cliente, producto, saldo_total,
                estatus, dia_pago, zona_geografica, telefonos) FROM STDIN (FORMAT BINARY)
            """, cancellationToken);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index]; await copy.StartRowAsync(cancellationToken);
            await copy.WriteAsync(id, NpgsqlDbType.Uuid, cancellationToken); await copy.WriteAsync(index, NpgsqlDbType.Integer, cancellationToken);
            await WriteNullableAsync(copy, row.ClientId, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.ClientName, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.Product, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.TotalBalance, NpgsqlDbType.Numeric, cancellationToken);
            await WriteNullableAsync(copy, row.Status, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.PaymentDay, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.Zone, NpgsqlDbType.Text, cancellationToken);
            await copy.WriteAsync(JsonSerializer.Serialize(row.Phones), NpgsqlDbType.Jsonb, cancellationToken);
        }
        await copy.CompleteAsync(cancellationToken);
    }

    private static async Task CopyPaymentsAsync(NpgsqlConnection connection, Guid id, IReadOnlyList<ProcessedPayment> rows, CancellationToken cancellationToken)
    {
        await using var copy = await connection.BeginBinaryImportAsync("""
            COPY crm_proceso_pagos (id_ejecucion, id_pago_origen, cliente_unico, fecha_recepcion, recuperacion_por_gestion,
                semana, producto, concepto_calculo, plan_de_pago, zona_geografica, agente, agente_id, nombre_cliente,
                dia_semana, id_promesa_asignada, id_promesa_base, tipo_asignacion) FROM STDIN (FORMAT BINARY)
            """, cancellationToken);
        foreach (var row in rows)
        {
            await copy.StartRowAsync(cancellationToken); await copy.WriteAsync(id, NpgsqlDbType.Uuid, cancellationToken);
            await copy.WriteAsync(row.SourceId, NpgsqlDbType.Bigint, cancellationToken); await WriteNullableAsync(copy, row.ClientId, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.ReceivedOn, NpgsqlDbType.Date, cancellationToken); await WriteNullableAsync(copy, row.Recovery, NpgsqlDbType.Numeric, cancellationToken);
            await WriteNullableAsync(copy, row.Week, NpgsqlDbType.Integer, cancellationToken); await WriteNullableAsync(copy, row.Product, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.Concept, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.PaymentPlan, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.Zone, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.Agent, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.AgentId, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.ClientName, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.Day, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.PromiseId, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.DatabasePromiseId, NpgsqlDbType.Integer, cancellationToken); await WriteNullableAsync(copy, row.AssignmentType, NpgsqlDbType.Text, cancellationToken);
        }
        await copy.CompleteAsync(cancellationToken);
    }

    private static async Task CopyPromisesAsync(NpgsqlConnection connection, Guid id, IReadOnlyList<ProcessedPromise> rows, CancellationToken cancellationToken)
    {
        await using var copy = await connection.BeginBinaryImportAsync("""
            COPY crm_proceso_promesas (id_ejecucion, id_promesa_base, id_promesa, id_promesa_ascendra, cliente_unico,
                nombre_cliente, numero_cliente, agente, agente_id, fecha_promesa, monto_inicial, monto_semanal,
                tipo_promesa, numero_semanas, fecha_creacion, estatus) FROM STDIN (FORMAT BINARY)
            """, cancellationToken);
        foreach (var row in rows)
        {
            await copy.StartRowAsync(cancellationToken); await copy.WriteAsync(id, NpgsqlDbType.Uuid, cancellationToken);
            await copy.WriteAsync(row.DatabasePromiseId, NpgsqlDbType.Integer, cancellationToken); await WriteNullableAsync(copy, row.VisibleId, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.AscendraRegistrationId, NpgsqlDbType.Integer, cancellationToken); await WriteNullableAsync(copy, row.ClientId, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.ClientName, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.ClientNumber, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.Agent, NpgsqlDbType.Text, cancellationToken); await WriteNullableAsync(copy, row.AgentId, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.PromiseDate, NpgsqlDbType.Date, cancellationToken); await WriteNullableAsync(copy, row.InitialAmount, NpgsqlDbType.Numeric, cancellationToken);
            await WriteNullableAsync(copy, row.WeeklyAmount, NpgsqlDbType.Numeric, cancellationToken); await WriteNullableAsync(copy, row.PromiseType, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(copy, row.NumberOfWeeks, NpgsqlDbType.Integer, cancellationToken); await WriteNullableAsync(copy, row.CreatedAt, NpgsqlDbType.Timestamp, cancellationToken);
            await WriteNullableAsync(copy, row.Status, NpgsqlDbType.Text, cancellationToken);
        }
        await copy.CompleteAsync(cancellationToken);
    }

    /// <summary>
    /// La cartera diaria determina actividad, pero no destruye la libreta:
    /// teléfonos y referencias que desaparecen de la cartera siguen siendo
    /// buscables como Inactivos. Las filas del corte conservan además el
    /// historial de producto, saldo y zona por ejecución.
    /// </summary>
    private static async Task PublishClientAndContactHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE crm_clientes_operativos SET activo=false, id_ejecucion_actual=@id, actualizado_en=now();

            INSERT INTO crm_clientes_operativos (cliente_unico, activo, id_ejecucion_actual, primera_visto_en, actualizado_en)
            SELECT DISTINCT cliente_unico, false, @id, now(), now()
            FROM crm_proceso_contactos WHERE id_ejecucion=@id AND nullif(btrim(cliente_unico), '') IS NOT NULL
            ON CONFLICT (cliente_unico) DO UPDATE SET id_ejecucion_actual=excluded.id_ejecucion_actual, actualizado_en=excluded.actualizado_en;

            INSERT INTO crm_clientes_operativos (cliente_unico, activo, id_ejecucion_actual, primera_visto_en, actualizado_en)
            SELECT DISTINCT cliente_unico, true, @id, now(), now()
            FROM crm_proceso_cartera WHERE id_ejecucion=@id AND nullif(btrim(cliente_unico), '') IS NOT NULL
            ON CONFLICT (cliente_unico) DO UPDATE SET activo=true, id_ejecucion_actual=excluded.id_ejecucion_actual,
                actualizado_en=excluded.actualizado_en;

            INSERT INTO crm_contactos_operativos (
                clave_contacto, cliente_unico, telefono, nombre_referencia, parentesco, origen,
                primera_ejecucion, ultima_ejecucion, primera_visto_en, ultimo_visto_en)
            SELECT concat_ws(E'\\x1f', cliente_unico, telefono, nombre_referencia, parentesco, origen),
                   cliente_unico, telefono, nombre_referencia, parentesco, origen, @id, @id, now(), now()
            FROM crm_proceso_contactos
            WHERE id_ejecucion=@id AND nullif(btrim(cliente_unico), '') IS NOT NULL AND nullif(btrim(telefono), '') IS NOT NULL
            ON CONFLICT (clave_contacto) DO UPDATE SET ultima_ejecucion=excluded.ultima_ejecucion,
                ultimo_visto_en=excluded.ultimo_visto_en;
            """, cancellationToken, ("id", id));
    }

    private static async Task AuditAndApplyStatusesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO promesas_historial (id_promesa, accion, registrado_en, registrado_por, datos_anteriores, datos_nuevos)
            SELECT promise.id_promesa, 'Conciliación automática', now(), 'Proceso automático', to_jsonb(promise),
                jsonb_set(to_jsonb(promise), '{estatus}', to_jsonb(result.estatus), true)
                    || jsonb_build_object('id_ejecucion_conciliacion', @id::text)
            FROM promesas promise
            JOIN crm_proceso_promesas result ON result.id_promesa_base=promise.id_promesa AND result.id_ejecucion=@id
            WHERE promise.estatus IS DISTINCT FROM result.estatus;
            UPDATE promesas promise SET estatus=result.estatus
            FROM crm_proceso_promesas result
            WHERE result.id_ejecucion=@id AND result.id_promesa_base=promise.id_promesa
              AND promise.estatus IS DISTINCT FROM result.estatus;
            """, cancellationToken, ("id", id));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteNullableAsync<T>(NpgsqlBinaryImporter copy, T? value, NpgsqlDbType type,
        CancellationToken cancellationToken) where T : struct
    {
        if (value is null) await copy.WriteNullAsync(cancellationToken);
        else await copy.WriteAsync(value.Value, type, cancellationToken);
    }
    private static async Task WriteNullableAsync(NpgsqlBinaryImporter copy, string? value, NpgsqlDbType type,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(value)) await copy.WriteNullAsync(cancellationToken);
        else await copy.WriteAsync(value, type, cancellationToken);
    }

    private static string? Text(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        return null;
    }
    private static decimal? Decimal(JsonElement row, params string[] names) => decimal.TryParse(Text(row, names), NumberStyles.Number,
        CultureInfo.InvariantCulture, out var value) ? value : null;
    private static int? Integer(JsonElement row, params string[] names) => int.TryParse(Text(row, names), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) ? value : null;
    private static DateOnly? Date(JsonElement row, params string[] names) => DateOnly.TryParse(Text(row, names), CultureInfo.InvariantCulture,
        DateTimeStyles.AllowWhiteSpaces, out var value) ? value : null;
    private static string? NullableText(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static decimal? NullableDecimal(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetDecimal(index);
    private static DateOnly? NullableDate(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetFieldValue<DateOnly>(index);
    private sealed record RawRow(long Id, JsonElement Data);
}
