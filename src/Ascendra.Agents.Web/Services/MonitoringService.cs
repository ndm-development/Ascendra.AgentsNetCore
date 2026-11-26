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
public sealed class MonitoringService(PostgresDatabase database, ExcelSnapshotStore snapshots)
{
    public async Task<MonitoringSnapshot> GetSnapshotAsync(
        MonitoringFilters filters,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rawAgents = await ReadAgentIdentitiesAsync(connection, """
            SELECT agent_id, agent_name
            FROM (
                SELECT COALESCE(NULLIF(BTRIM(agent_id), ''), '') AS agent_id,
                       COALESCE(NULLIF(BTRIM(agent_name), ''), 'Sin agente') AS agent_name
                FROM call_dispositions
                UNION
                SELECT COALESCE(NULLIF(BTRIM(usu_alta), ''), '') AS agent_id,
                       COALESCE(NULLIF(BTRIM(agente), ''), 'Sin agente') AS agent_name
                FROM promesas
            ) AS agents;
            """, cancellationToken);
        var agentCatalog = new MonitoringAgentCatalog(snapshots.Current, rawAgents);
        var agents = agentCatalog.ListAgents();
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

        var dispositions = agentCatalog.Aggregate(
            await ReadDispositionCountsAsync(connection, filters, cancellationToken), filters.AgentKey);
        var promises = agentCatalog.Aggregate(
            await ReadPromiseCountsAsync(connection, filters, cancellationToken), filters.AgentKey);
        return new MonitoringSnapshot(agents, promiseTypes, promiseStatuses, categories, channels, dispositions, promises);
    }

    public ChartModel AgentCountChart(
        IReadOnlyList<MonitoringAgentCount> counts,
        string title,
        string traceName,
        string color)
    {
        // La gráfica es operativa: excluir agentes con menor volumen haría
        // parecer que sus gestiones no se registraron. Mostramos a todas las
        // personas que tienen actividad en el periodo seleccionado.
        var visible = counts.Reverse().ToArray();
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
                height = Math.Max(330, visible.Length * 34 + 130),
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

    private static async Task<IReadOnlyList<RawAgentIdentity>> ReadAgentIdentitiesAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var identities = new List<RawAgentIdentity>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            identities.Add(new RawAgentIdentity(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? "Sin agente" : reader.GetString(1)));
        }
        return identities;
    }

    private static async Task<IReadOnlyList<RawAgentCount>> ReadDispositionCountsAsync(
        NpgsqlConnection connection,
        MonitoringFilters filters,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(NULLIF(BTRIM(agent_id), ''), ''),
                   COALESCE(NULLIF(BTRIM(agent_name), ''), 'Sin agente'),
                   COUNT(*)
            FROM call_dispositions
            WHERE (@start_instant IS NULL OR recorded_at >= @start_instant)
              AND (@end_exclusive IS NULL OR recorded_at < @end_exclusive)
              AND (@category IS NULL OR LOWER(category) = LOWER(@category))
              AND (@channel IS NULL OR LOWER(channel) = LOWER(@channel))
            GROUP BY 1, 2
            ORDER BY COUNT(*) DESC, 2;
            """, connection);
        AddDateParameters(command, filters);
        AddOptionalText(command, "category", filters.DispositionCategory);
        AddOptionalText(command, "channel", filters.DispositionChannel);
        return await ReadRawCountsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<RawAgentCount>> ReadPromiseCountsAsync(
        NpgsqlConnection connection,
        MonitoringFilters filters,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(NULLIF(BTRIM(usu_alta), ''), ''),
                   COALESCE(NULLIF(BTRIM(agente), ''), 'Sin agente'),
                   COUNT(*)
            FROM promesas
            WHERE (@start_instant IS NULL OR fecha_creacion >= @start_instant)
              AND (@end_exclusive IS NULL OR fecha_creacion < @end_exclusive)
              AND (@promise_type IS NULL OR LOWER(tipo_promesa) = LOWER(@promise_type))
              AND (@promise_status IS NULL OR LOWER(estatus) = LOWER(@promise_status))
            GROUP BY 1, 2
            ORDER BY COUNT(*) DESC, 2;
            """, connection);
        AddDateParameters(command, filters);
        AddOptionalText(command, "promise_type", filters.PromiseType);
        AddOptionalText(command, "promise_status", filters.PromiseStatus);
        return await ReadRawCountsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<RawAgentCount>> ReadRawCountsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var counts = new List<RawAgentCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new RawAgentCount(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? "Sin agente" : reader.GetString(1),
                checked((int)reader.GetInt64(2))));
        }
        return counts;
    }

    private static void AddDateParameters(NpgsqlCommand command, MonitoringFilters filters)
    {
        // Los eventos se almacenan en UTC, pero el rango seleccionado se
        // interpreta como días completos de Ciudad de México. Además de
        // eliminar el salto de fecha nocturno, el rango sobre timestamptz
        // permite usar los índices de las dos tablas.
        command.Parameters.Add(new NpgsqlParameter("start_instant", NpgsqlDbType.TimestampTz)
        {
            Value = filters.StartDate is null
                ? DBNull.Value
                : MexicoCityMidnight(filters.StartDate.Value),
        });
        command.Parameters.Add(new NpgsqlParameter("end_exclusive", NpgsqlDbType.TimestampTz)
        {
            Value = filters.EndDate is null
                ? DBNull.Value
                : MexicoCityMidnight(filters.EndDate.Value.AddDays(1)),
        });
    }

    private static DateTimeOffset MexicoCityMidnight(DateOnly date) =>
        OperationalClock.FromMexicoCityLocal(date.ToDateTime(TimeOnly.MinValue)).ToUniversalTime();

    private static void AddOptionalText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim(),
        });

    private sealed record RawAgentIdentity(string AgentId, string AgentName);
    private sealed record RawAgentCount(string AgentId, string AgentName, int Count);

    /// <summary>
    /// Convierte los nombres históricos en una identidad estable. El ID es la
    /// llave primaria de la persona; los nombres sólo son la etiqueta humana.
    /// Esto evita que cambios de mayúsculas, acentos o un segundo apellido
    /// creen agentes distintos en los indicadores.
    /// </summary>
    private sealed class MonitoringAgentCatalog
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _namesById;
        private readonly IReadOnlyDictionary<string, string> _canonicalNamesById;
        private readonly IReadOnlyList<RawAgentIdentity> _knownAliases;

        public MonitoringAgentCatalog(AgentDataSnapshot snapshot, IReadOnlyList<RawAgentIdentity> rawAgents)
        {
            var known = new List<RawAgentIdentity>(rawAgents);
            known.AddRange(snapshot.Users.Select(user => new RawAgentIdentity(user.Id, user.DisplayName)));
            known.AddRange(snapshot.AgentDirectory.SelectMany(agent => agent.Aliases
                .Select(alias => new RawAgentIdentity(agent.Id, alias))));
            _knownAliases = known
                .Where(alias => HasAgentId(alias.AgentId) && HasAgentName(alias.AgentName))
                .ToArray();
            _canonicalNamesById = snapshot.AgentDirectory
                .Where(agent => HasAgentId(agent.Id) && HasAgentName(agent.DisplayName))
                .GroupBy(agent => NormalizeAgentId(agent.Id), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().DisplayName.Trim(),
                    StringComparer.OrdinalIgnoreCase);
            _namesById = _knownAliases
                .GroupBy(alias => NormalizeAgentId(alias.AgentId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Select(alias => alias.AgentName).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<MonitoringAgent> ListAgents() => _knownAliases
            .Select(Resolve)
            .GroupBy(identity => identity.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MonitoringAgent(
                group.Key,
                DisplayName(group.Key, group.Select(identity => identity.SourceName))))
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        public IReadOnlyList<MonitoringAgentCount> Aggregate(
            IReadOnlyList<RawAgentCount> rawCounts,
            string? selectedAgentKey) => rawCounts
            .Select(row => new { Identity = Resolve(new RawAgentIdentity(row.AgentId, row.AgentName)), row.Count })
            .Where(row => string.IsNullOrWhiteSpace(selectedAgentKey)
                || row.Identity.Key.Equals(selectedAgentKey.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.Identity.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MonitoringAgentCount(
                group.Key,
                DisplayName(group.Key, group.Select(row => row.Identity.SourceName)),
                group.Sum(row => row.Count)))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.AgentName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        private AgentIdentity Resolve(RawAgentIdentity raw)
        {
            var agentId = NormalizeAgentId(raw.AgentId);
            if (HasAgentId(agentId)) return new AgentIdentity($"id:{agentId}", raw.AgentName);

            var possibleIds = _knownAliases
                .Where(alias => NamesReferToSamePerson(raw.AgentName, alias.AgentName))
                .Select(alias => NormalizeAgentId(alias.AgentId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (possibleIds.Length == 1) return new AgentIdentity($"id:{possibleIds[0]}", raw.AgentName);

            var normalizedName = TextNormalization.Normalize(raw.AgentName);
            return new AgentIdentity(
                normalizedName.Length == 0 || normalizedName == "sin agente"
                    ? "name:sin-agente"
                    : $"name:{normalizedName}",
                raw.AgentName);
        }

        private string DisplayName(string key, IEnumerable<string> sourceNames)
        {
            if (key.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                && _canonicalNamesById.TryGetValue(key[3..], out var canonicalName))
            {
                return canonicalName;
            }

            var candidates = sourceNames
                .Concat(key.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                    && _namesById.TryGetValue(key[3..], out var aliases)
                        ? aliases
                        : [])
                .Where(HasAgentName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(NameTokenCount)
                .ThenByDescending(name => name.Trim().Length)
                .ThenBy(IsAllUppercase)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return candidates.FirstOrDefault()?.Trim() ?? "Sin agente";
        }

        private static bool NamesReferToSamePerson(string left, string right)
        {
            var leftTokens = TextNormalization.Normalize(left)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var rightTokens = TextNormalization.Normalize(right)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (leftTokens.Length < 2 || rightTokens.Length < 2) return false;
            return leftTokens.All(token => rightTokens.Contains(token, StringComparer.Ordinal))
                || rightTokens.All(token => leftTokens.Contains(token, StringComparer.Ordinal));
        }

        private static string NormalizeAgentId(string? value) => TextNormalization.AgentId(value);
        private static bool HasAgentId(string? value) => !string.IsNullOrWhiteSpace(value)
            && !TextNormalization.Normalize(value).Equals("sin id", StringComparison.Ordinal);
        private static bool HasAgentName(string? value) => !string.IsNullOrWhiteSpace(value)
            && !TextNormalization.Normalize(value).Equals("sin agente", StringComparison.Ordinal);
        private static int NameTokenCount(string value) => TextNormalization.Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        private static bool IsAllUppercase(string value) => value.Any(char.IsLetter)
            && value.Where(char.IsLetter).All(char.IsUpper);

        private sealed record AgentIdentity(string Key, string SourceName);
    }
}
