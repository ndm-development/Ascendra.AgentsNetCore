using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Ascendra.Agents.Infrastructure;

/// <summary>Carga transaccional de fuentes. No actualiza promesas, roles ni datos operativos existentes.</summary>
public sealed class PostgresOperationalImportRepository(NpgsqlDataSource dataSource) : IOperationalImportRepository
{
    public async Task<DataImportResult> ImportAsync(DataImportBatch batch, CancellationToken cancellationToken = default)
    {
        var table = OperationalDatasets.Table(batch.Dataset);
        if (batch.Columns.Count == 0 || batch.Columns.Select(column => column.Name).Distinct().Count() != batch.Columns.Count
            || batch.Columns.Any(column => !Regex.IsMatch(column.Name, "^[a-z][a-z0-9_]{0,54}$")))
            throw new InvalidOperationException("Los encabezados no son válidos para la importación.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        async Task Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await Execute("SET LOCAL lock_timeout = '5s'; SELECT pg_advisory_xact_lock(831731, 2);");
        await Execute(OperationalImportSchema.Sql);
        // Registrar esta migración sólo si el registro del CRM ya existe; el importador también funciona en una base vacía.
        await Execute("""
            DO $$ BEGIN
                IF to_regclass('public.application_schema_migrations') IS NOT NULL THEN
                    INSERT INTO public.application_schema_migrations (migration_id, applied_at)
                    VALUES ('2026-11-27-02-operational-imports', now()) ON CONFLICT DO NOTHING;
                END IF;
            END $$;
            """);
        await Execute($"""
            CREATE TABLE IF NOT EXISTS public.{Quote(table)} (
                _id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                _lote_id uuid NULL REFERENCES crm_import_lotes(id_lote),
                _fila_origen integer NULL,
                _incidencias jsonb NULL,
                UNIQUE (_lote_id, _fila_origen)
            );
            """);
        await Execute(string.Join('\n', batch.Columns.Select(column =>
            $"ALTER TABLE public.{Quote(table)} ADD COLUMN IF NOT EXISTS {Quote(column.Name)} {SqlType(column.Type)} NULL;")));
        await ValidateSchemaAsync(connection, transaction, table, batch.Columns, cancellationToken);
        await Execute($"""
            CREATE OR REPLACE VIEW public.{Quote(table + "_vigentes")} AS
            SELECT lote.archivo_origen AS _archivo_origen, lote.hoja_origen AS _hoja_origen,
                lote.cargado_en AS _cargado_en, fila.*
            FROM public.{Quote(table)} fila JOIN crm_import_lotes lote ON fila._lote_id = lote.id_lote
            WHERE lote.vigente IS TRUE;
            """);

        Guid? existingId = null;
        var current = false;
        await using (var existing = new NpgsqlCommand("""
            SELECT id_lote, vigente FROM crm_import_lotes
            WHERE conjunto=@dataset AND clave_origen=@source AND sha256=@hash AND version_lector=1;
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("dataset", batch.Dataset);
            existing.Parameters.AddWithValue("source", batch.SourceKey);
            existing.Parameters.AddWithValue("hash", batch.Fingerprint);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) { existingId = reader.GetGuid(0); current = !reader.IsDBNull(1) && reader.GetBoolean(1); }
        }
        if (existingId is not null)
        {
            await VerifyAsync(connection, transaction, table, existingId.Value, batch, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(batch.Dataset, batch.SourceKey, batch.Rows.Count, Imported: false, Current: current);
        }
        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO crm_import_lotes
                (id_lote, conjunto, clave_origen, archivo_origen, hoja_origen, sha256, version_lector, filas, columnas, vigente, cargado_en)
            VALUES (@id, @dataset, @source, @file, @sheet, @hash, 1, @rows, @columns, false, now());
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("dataset", batch.Dataset);
            insert.Parameters.AddWithValue("source", batch.SourceKey);
            insert.Parameters.AddWithValue("file", batch.FileName);
            insert.Parameters.AddWithValue("sheet", batch.SheetName);
            insert.Parameters.AddWithValue("hash", batch.Fingerprint);
            insert.Parameters.AddWithValue("rows", batch.Rows.Count);
            insert.Parameters.AddWithValue("columns", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(batch.Columns));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var names = string.Join(", ", batch.Columns.Select(column => Quote(column.Name)));
        await using (var copy = await connection.BeginBinaryImportAsync(
            $"COPY public.{Quote(table)} (_lote_id, _fila_origen, _incidencias, {names}) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var row in batch.Rows)
            {
                await copy.StartRowAsync(cancellationToken);
                await copy.WriteAsync(id, NpgsqlDbType.Uuid, cancellationToken);
                await copy.WriteAsync(row.SourceRow, NpgsqlDbType.Integer, cancellationToken);
                if (row.Issues is null) await copy.WriteNullAsync(cancellationToken);
                else await copy.WriteAsync(row.Issues, NpgsqlDbType.Jsonb, cancellationToken);
                for (var index = 0; index < batch.Columns.Count; index++)
                {
                    var value = row.Values[index];
                    if (value is null) await copy.WriteNullAsync(cancellationToken);
                    else await copy.WriteAsync(value, DbType(batch.Columns[index].Type), cancellationToken);
                }
            }
            await copy.CompleteAsync(cancellationToken);
        }
        await VerifyAsync(connection, transaction, table, id, batch, cancellationToken);
        await using (var publish = new NpgsqlCommand("""
            UPDATE crm_import_lotes SET vigente=false WHERE conjunto=@dataset AND clave_origen=@source AND vigente IS TRUE;
            UPDATE crm_import_lotes SET vigente=true WHERE id_lote=@id;
            """, connection, transaction))
        {
            publish.Parameters.AddWithValue("dataset", batch.Dataset);
            publish.Parameters.AddWithValue("source", batch.SourceKey);
            publish.Parameters.AddWithValue("id", id);
            await publish.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(batch.Dataset, batch.SourceKey, batch.Rows.Count, Imported: true, Current: true);
    }

    private static async Task VerifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table,
        Guid id, DataImportBatch batch, CancellationToken cancellationToken)
    {
        var amountIndex = batch.Columns.ToList().FindIndex(column => column.Name == "recuperacion_por_gestion");
        var amountSql = amountIndex >= 0 ? ", COALESCE(SUM(recuperacion_por_gestion), 0)" : string.Empty;
        await using var command = new NpgsqlCommand($"SELECT COUNT(*){amountSql} FROM public.{Quote(table)} WHERE _lote_id=@id;", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.GetInt64(0) != batch.Rows.Count) throw new InvalidOperationException("La cantidad persistida no coincide con el archivo.");
        if (amountIndex >= 0 && reader.GetDecimal(1) != batch.Rows.Sum(row => row.Values[amountIndex] as decimal? ?? 0m))
            throw new InvalidOperationException("El total de recuperación no coincide con el archivo.");
    }

    private static async Task ValidateSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string table, IReadOnlyList<ImportColumn> columns, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT column_name, data_type, is_nullable FROM information_schema.columns
            WHERE table_schema='public' AND table_name=@table;
            """, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var column = columns.FirstOrDefault(value => value.Name == name);
            if (name != "_id" && reader.GetString(2) != "YES")
                throw new InvalidOperationException($"La columna {table}.{name} no admite nulos.");
            if (column is not null && reader.GetString(1) != SqlType(column.Type))
                throw new InvalidOperationException($"El tipo de {table}.{name} cambió respecto a la importación anterior.");
        }
    }

    private static string Quote(string name) => '"' + name + '"';
    private static string SqlType(ImportValueType type) => type switch
    {
        ImportValueType.Integer => "integer", ImportValueType.Decimal => "numeric",
        ImportValueType.Timestamp => "timestamp without time zone", _ => "text"
    };
    private static NpgsqlDbType DbType(ImportValueType type) => type switch
    {
        ImportValueType.Integer => NpgsqlDbType.Integer, ImportValueType.Decimal => NpgsqlDbType.Numeric,
        ImportValueType.Timestamp => NpgsqlDbType.Timestamp, _ => NpgsqlDbType.Text
    };
}
