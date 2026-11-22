using Ascendra.Agents.Web.Domain;
using Ascendra.Agents.Web.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Ascendra.Agents.Web.Services;

/// <summary>
/// Lecturas agregadas para Gerencia y Supervisión. Las gráficas consultan los
/// eventos reales de PostgreSQL y nunca exponen gestiones individuales fuera
/// de las pantallas autorizadas.
/// </summary>
public sealed class MonitoringService(PostgresDatabase database)
{
    public async Task<MonitoringSnapshot> GetSnapshotAsync(
        MonitoringFilters filters,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var agents = await ReadStringsAsync(connection, """
            SELECT agent_name
            FROM (
                SELECT NULLIF(BTRIM(agent_name), '') AS agent_name FROM call_dispositions
                UNION
                SELECT NULLIF(BTRIM(agente), '') AS agent_name FROM promesas
            ) AS agents
            WHERE agent_name IS NOT NULL
            ORDER BY agent_name;
            """, cancellationToken);
        var promiseTypes = await ReadStringsAsync(connection, """
            SELECT DISTINCT NULLIF(BTRIM(tipo_promesa), '')
            FROM promesas
            WHERE NULLIF(BTRIM(tipo_promesa), '') IS NOT NULL
            ORDER BY 1;
            """, cancellationToken);
        var promiseStatuses = await ReadStringsAsync(connection, """
            SELECT DISTINCT NULLIF(BTRIM(estatus), '')
            FROM promesas
            WHERE NULLIF(BTRIM(estatus), '') IS NOT NULL
            ORDER BY 1;
            """, cancellationToken);
        var categories = await ReadStringsAsync(connection, """
            SELECT DISTINCT NULLIF(BTRIM(category), '')
            FROM call_dispositions
            WHERE NULLIF(BTRIM(category), '') IS NOT NULL
            ORDER BY 1;
            """, cancellationToken);
        var channels = await ReadStringsAsync(connection, """
            SELECT DISTINCT NULLIF(BTRIM(channel), '')
            FROM call_dispositions
            WHERE NULLIF(BTRIM(channel), '') IS NOT NULL
            ORDER BY 1;
            """, cancellationToken);

        var dispositions = await ReadDispositionCountsAsync(connection, filters, cancellationToken);
        var promises = await ReadPromiseCountsAsync(connection, filters, cancellationToken);
        return new MonitoringSnapshot(agents, promiseTypes, promiseStatuses, categories, channels, dispositions, promises);
    }

    public ChartModel AgentCountChart(
        IReadOnlyList<MonitoringAgentCount> counts,
        string title,
        string traceName,
        string color)
    {
        var visible = counts.Take(30).Reverse().ToArray();
        var values = visible.Select(item => item.Count).ToArray();
        return new ChartModel(
            [new
            {
                type = "bar",
                orientation = "h",
                name = traceName,
                x = values,
                y = visible.Select(item => item.AgentName).ToArray(),
                text = values,
                textposition = "outside",
                cliponaxis = false,
                marker = new
                {
                    // Arreglo intencional: charts.js aplica la paleta de la
                    // apariencia elegida, incluido el acabado Kintsugi.
                    color = values.Select(_ => color).ToArray(),
                    line = new { color = "rgba(255,255,255,.58)", width = 1 },
                    cornerradius = 6,
                },
                hovertemplate = "<b>%{y}</b><br>%{x} registros<extra></extra>",
            }],
            new
            {
                title = new { text = title, x = .01, font = new { size = 17 } },
                height = Math.Clamp(visible.Length * 34 + 130, 330, 1080),
                margin = new { l = 175, r = 46, t = 64, b = 48 },
                paper_bgcolor = "rgba(0,0,0,0)",
                plot_bgcolor = "rgba(0,0,0,0)",
                showlegend = false,
                font = new { family = "-apple-system, BlinkMacSystemFont, 'SF Pro Text', sans-serif" },
                xaxis = new { title = "Registros", rangemode = "tozero", fixedrange = true },
                yaxis = new { fixedrange = true, automargin = true },
            },
            new { responsive = true, displayModeBar = false });
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0)) values.Add(reader.GetString(0));
        }
        return values;
    }

    private static async Task<IReadOnlyList<MonitoringAgentCount>> ReadDispositionCountsAsync(
        NpgsqlConnection connection,
        MonitoringFilters filters,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(NULLIF(BTRIM(agent_name), ''), 'Sin agente'), COUNT(*)
            FROM call_dispositions
            WHERE (@start_date IS NULL OR recorded_at::date >= @start_date)
              AND (@end_date IS NULL OR recorded_at::date <= @end_date)
              AND (@agent_name IS NULL OR LOWER(agent_name) = LOWER(@agent_name))
              AND (@category IS NULL OR LOWER(category) = LOWER(@category))
              AND (@channel IS NULL OR LOWER(channel) = LOWER(@channel))
            GROUP BY 1
            ORDER BY COUNT(*) DESC, 1;
            """, connection);
        AddDateAndAgentParameters(command, filters);
        AddOptionalText(command, "category", filters.DispositionCategory);
        AddOptionalText(command, "channel", filters.DispositionChannel);
        return await ReadCountsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<MonitoringAgentCount>> ReadPromiseCountsAsync(
        NpgsqlConnection connection,
        MonitoringFilters filters,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(NULLIF(BTRIM(agente), ''), 'Sin agente'), COUNT(*)
            FROM promesas
            WHERE (@start_date IS NULL OR fecha_creacion::date >= @start_date)
              AND (@end_date IS NULL OR fecha_creacion::date <= @end_date)
              AND (@agent_name IS NULL OR LOWER(agente) = LOWER(@agent_name))
              AND (@promise_type IS NULL OR LOWER(tipo_promesa) = LOWER(@promise_type))
              AND (@promise_status IS NULL OR LOWER(estatus) = LOWER(@promise_status))
            GROUP BY 1
            ORDER BY COUNT(*) DESC, 1;
            """, connection);
        AddDateAndAgentParameters(command, filters);
        AddOptionalText(command, "promise_type", filters.PromiseType);
        AddOptionalText(command, "promise_status", filters.PromiseStatus);
        return await ReadCountsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<MonitoringAgentCount>> ReadCountsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var counts = new List<MonitoringAgentCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new MonitoringAgentCount(reader.GetString(0), checked((int)reader.GetInt64(1))));
        }
        return counts;
    }

    private static void AddDateAndAgentParameters(NpgsqlCommand command, MonitoringFilters filters)
    {
        command.Parameters.Add(new NpgsqlParameter("start_date", NpgsqlDbType.Date)
        {
            Value = filters.StartDate is null ? DBNull.Value : filters.StartDate.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("end_date", NpgsqlDbType.Date)
        {
            Value = filters.EndDate is null ? DBNull.Value : filters.EndDate.Value,
        });
        AddOptionalText(command, "agent_name", filters.AgentName);
    }

    private static void AddOptionalText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim(),
        });
}
