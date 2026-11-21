using Ascendra.Agents.Web.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Ascendra.Agents.Web.Services;

/// <summary>
/// Migra una sola vez la fuente histórica de promesas. El archivo original
/// se conserva sin cambios y se omiten únicamente las filas que la dirección
/// indicó no utilizar: semanas en cero e importes negativos.
/// </summary>
public sealed class HistoricalPromiseImportService(
    IHostEnvironment environment,
    PostgresDatabase database,
    ILogger<HistoricalPromiseImportService> logger) : IHostedService
{
    private const string ImportKey = "promesas-historicas-2026-11-20-v1";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(environment.ContentRootPath, "data", "input", "promesas_historicas.xlsx");
        if (!File.Exists(path))
        {
            logger.LogInformation("No se encontró el archivo de promesas históricas; la migración se omitió.");
            return;
        }
        if (await database.HasCompletedImportAsync(ImportKey, cancellationToken)) return;

        var rows = WorkbookTable.Read(path).Rows;
        var imported = 0;
        var excludedWeeks = 0;
        var excludedAmounts = 0;
        var excludedRequiredData = 0;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO promesas (
                    cliente_unico, agente, numero_cliente, fecha_promesa,
                    monto_inicial, monto_semanal, tipo_promesa, numero_semanas,
                    fecha_creacion, usu_alta, origen, id_promesa_origen)
                VALUES (
                    @clientId, @agent, @clientNumber, @promiseDate,
                    @initialAmount, @weeklyAmount, @promiseType, @numberOfWeeks,
                    @createdAt, @agentId, 'Historico', @sourceId)
                ON CONFLICT DO NOTHING;
                """, connection, transaction);
            command.Parameters.Add("clientId", NpgsqlDbType.Varchar);
            command.Parameters.Add("agent", NpgsqlDbType.Varchar);
            command.Parameters.Add("clientNumber", NpgsqlDbType.Varchar);
            command.Parameters.Add("promiseDate", NpgsqlDbType.Date);
            command.Parameters.Add("initialAmount", NpgsqlDbType.Numeric);
            command.Parameters.Add("weeklyAmount", NpgsqlDbType.Numeric);
            command.Parameters.Add("promiseType", NpgsqlDbType.Varchar);
            command.Parameters.Add("numberOfWeeks", NpgsqlDbType.Smallint);
            command.Parameters.Add("createdAt", NpgsqlDbType.TimestampTz);
            command.Parameters.Add("agentId", NpgsqlDbType.Varchar);
            command.Parameters.Add("sourceId", NpgsqlDbType.Varchar);

            foreach (var row in rows)
            {
                var weeks = row.Integer("Numero_Semanas", "Número de semanas", "Numero Semanas");
                if (weeks is null || weeks < 1 || weeks > 255)
                {
                    excludedWeeks++;
                    continue;
                }

                var initialAmount = row.OptionalDecimal("Monto_Inicial", "Monto Inicial");
                var weeklyAmount = row.OptionalDecimal("Monto_Semanal", "Monto Semanal");
                if (initialAmount < 0m || weeklyAmount < 0m)
                {
                    excludedAmounts++;
                    continue;
                }

                var sourceId = row.Text("IdPromesa", "ID Promesa");
                var clientId = row.Text("Cliente_Unico", "CLIENTE_UNICO");
                var clientNumber = row.Text("Numero_Cliente", "Número Cliente");
                var promiseDate = row.Date("Fecha_Promesa", "Fecha Promesa");
                var createdAt = row.Timestamp("Fecha_Creacion", "Fecha Creacion");
                if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(clientId)
                    || string.IsNullOrWhiteSpace(clientNumber) || promiseDate is null || createdAt is null)
                {
                    excludedRequiredData++;
                    continue;
                }

                command.Parameters["clientId"].Value = clientId;
                command.Parameters["agent"].Value = DbValue(row.Text("Agente"));
                command.Parameters["clientNumber"].Value = clientNumber;
                command.Parameters["promiseDate"].Value = promiseDate.Value;
                command.Parameters["initialAmount"].Value = DbValue(initialAmount);
                command.Parameters["weeklyAmount"].Value = DbValue(weeklyAmount);
                command.Parameters["promiseType"].Value = DbValue(NormalizePromiseType(row.Text("Tipo_Promesa", "Tipo Promesa")));
                command.Parameters["numberOfWeeks"].Value = checked((short)weeks.Value);
                command.Parameters["createdAt"].Value = OperationalClock.FromMexicoCityLocal(createdAt.Value);
                command.Parameters["agentId"].Value = DbValue(row.Text("UsuAlta", "Agente_ID", "Agente ID"));
                command.Parameters["sourceId"].Value = sourceId;
                imported += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            await database.MarkImportCompletedAsync(ImportKey, cancellationToken);
            logger.LogInformation(
                "Migración histórica de promesas terminada. Importadas: {Imported}; semanas excluidas: {Weeks}; importes negativos excluidos: {Amounts}; filas incompletas excluidas: {RequiredData}.",
                imported, excludedWeeks, excludedAmounts, excludedRequiredData);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static object DbValue(decimal? value) => value.HasValue ? value.Value : DBNull.Value;
    private static string? NormalizePromiseType(string? value) => TextNormalization.Normalize(value) switch
    {
        "liquidacion" => "Liquidación",
        "parcial" => "Parcial",
        "plan de pago" => "Plan de Pago",
        "recurrencia" => "Recurrencia",
        "rmd" => "RMD",
        _ => string.IsNullOrWhiteSpace(value) ? null : value.Trim(),
    };
}
