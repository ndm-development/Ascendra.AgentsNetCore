using System.Globalization;
using Ascendra.Agents.Web.Domain;
using Ascendra.Agents.Web.Infrastructure;

namespace Ascendra.Agents.Web.Services;

/// <summary>
/// Consultas y figuras que replican el contrato operativo de Dash. La fuente
/// continúa siendo el mismo corte Excel; ninguna agregación se persiste en UI.
/// </summary>
public sealed class DashboardQueries(ExcelSnapshotStore snapshots, DashboardSession session)
{
    public static readonly string[] Days = ["Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo"];

    private static readonly (string Label, decimal Threshold, decimal Bonus)[] BonusTiers =
    [
        ("Junior", 20_000m, 500m), ("Élite", 48_000m, 1_200m),
        ("Superior", 60_000m, 1_800m), ("Superior", 70_000m, 2_800m),
        ("Exclusivo", 80_000m, 3_800m), ("Exclusivo", 90_000m, 4_800m),
        ("Luxe", 100_000m, 5_800m), ("Luxe", 110_000m, 6_800m),
        ("Luxe", 120_000m, 7_800m), ("Luxe", 130_000m, 8_800m),
        ("Épico", 140_000m, 9_800m), ("Épico", 150_000m, 10_800m),
        ("Épico", 160_000m, 11_800m), ("Épico", 170_000m, 12_800m),
        ("Legendario", 180_000m, 13_800m), ("Legendario", 190_000m, 14_800m),
        ("Legendario", 200_000m, 15_800m),
    ];

    public AgentDataSnapshot Snapshot => snapshots.Current;

    public ScopedOperations Scope()
    {
        var user = session.User;
        if (user is null) return new ScopedOperations([], [], [], []);

        bool Owns(string sourceAgent, string sourceId) =>
            (!string.IsNullOrWhiteSpace(sourceId) && sourceId == user.Id)
            || (string.IsNullOrWhiteSpace(sourceId) && TextNormalization.AgentNameMatches(sourceAgent, user.DisplayName));

        var payments = Snapshot.Payments.Where(x => Owns(x.Agent, x.AgentId)).ToArray();
        var promises = Snapshot.Promises.Where(x => Owns(x.Agent, x.AgentId)).ToArray();
        var allowedClients = payments.Select(x => x.ClientId).Concat(promises.Select(x => x.ClientId))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return BuildScope(payments, promises, Snapshot.Portfolio.Where(x => allowedClients.Contains(x.ClientId)).ToArray());
    }

    /// <summary>Aplica el buscador de cliente que comparten Mi recuperación y Mis promesas.</summary>
    public ScopedOperations FilterByClient(ScopedOperations scope, string? clientQuery)
    {
        if (string.IsNullOrWhiteSpace(clientQuery)) return scope;
        var query = clientQuery.Trim();
        var payments = scope.Payments.Where(x => x.ClientId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var promises = scope.Promises.Where(x => x.ClientId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var allowed = payments.Select(x => x.ClientId).Concat(promises.Select(x => x.ClientId))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return BuildScope(payments, promises, scope.Portfolio.Where(x => allowed.Contains(x.ClientId)).ToArray());
    }

    public int CurrentWeek(ScopedOperations scope)
    {
        if (scope.Weeks.Count == 0) return 0;
        var currentIsoWeek = ISOWeek.GetWeekOfYear(DateTime.Today);
        return scope.Weeks.Where(week => week <= currentIsoWeek).DefaultIfEmpty(scope.Weeks[^1]).Last();
    }

    public IReadOnlyList<string> AgentNames(ScopedOperations scope) => scope.Payments.Select(x => x.Agent)
        .Concat(scope.Promises.Select(x => x.Agent))
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<string> PromiseTypes(ScopedOperations scope) => scope.Promises.Select(x => x.Type)
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<string> PromiseStatuses(ScopedOperations scope) => scope.Promises.Select(x => x.Status)
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public ScopedOperations FilterAgentStatus(ScopedOperations scope, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return scope;
        bool IsActive(string agent) => Snapshot.AgentDirectory.Any(directory =>
            TextNormalization.AgentNameMatches(agent, $"{directory.FirstName} {directory.PaternalSurname}"));
        var wantActive = status.Equals("Activo", StringComparison.OrdinalIgnoreCase);
        var payments = scope.Payments.Where(x => IsActive(x.Agent) == wantActive).ToArray();
        var promises = scope.Promises.Where(x => IsActive(x.Agent) == wantActive).ToArray();
        return BuildScope(payments, promises, scope.Portfolio);
    }

    public IReadOnlyList<ContactRecord> FindContacts(string query, bool byPhone)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (byPhone)
        {
            var phone = TextNormalization.Phone(query);
            var clientIds = Snapshot.Contacts.Where(x => x.Phone == phone).Select(x => x.ClientId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Snapshot.Contacts.Where(x => clientIds.Contains(x.ClientId)).OrderBy(x => x.ClientId).ToArray();
        }
        return Snapshot.Contacts.Where(x => x.ClientId.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public IReadOnlyList<ContactRecord> FindWhatsAppCandidates(string phone) => Snapshot.Contacts
        .Where(x => x.Phone == TextNormalization.Phone(phone))
        .GroupBy(x => x.ClientId, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(x => x.Status.Equals("Activo", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.ClientName)).First())
        .OrderBy(x => x.ClientName).ToArray();

    public string GreetingForWeek(int week)
    {
        var scope = Scope();
        var current = scope.Payments.Where(x => x.Week == week && x.Recovery > 0m).Sum(x => x.Recovery);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var elapsed = week == ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue)) ? (int)today.DayOfWeek : 7;
        if (elapsed == 0) elapsed = 7;
        var name = session.ShortName;
        if (current >= 6_000m * elapsed) return $"Buen ritmo, {name}. Sigue asi!.";
        if (current >= 5_000m * elapsed) return $"{name}, vas avanzando, pero aún puedes dar un paso más.";
        return $"{name}, todavía puedes corregir el rumbo: acelera tu cobranza.";
    }

    public WeeklyPulse WeeklyPulse(ScopedOperations scope, int week)
    {
        var payments = scope.Payments.Where(x => x.Week == week && x.Recovery > 0m).ToArray();
        var recovery = payments.Sum(x => x.Recovery);
        var currentIndex = Array.FindLastIndex(BonusTiers, tier => recovery >= tier.Threshold);
        var nextIndex = Array.FindIndex(BonusTiers, tier => recovery < tier.Threshold);
        var current = currentIndex >= 0 ? BonusTiers[currentIndex] : ((string Label, decimal Threshold, decimal Bonus)?)null;
        var next = nextIndex >= 0 ? BonusTiers[nextIndex] : ((string Label, decimal Threshold, decimal Bonus)?)null;
        var start = current?.Threshold ?? 0m;
        var progress = next is null ? 1d : next.Value.Threshold == start ? 0d : Math.Clamp((double)((recovery - start) / (next.Value.Threshold - start)), 0d, 1d);
        return new WeeklyPulse(
            recovery,
            payments.Length,
            payments.Select(x => x.ClientId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            current?.Label ?? "En camino a Junior",
            current?.Bonus ?? 0m,
            next is null ? null : Math.Max(next.Value.Threshold - recovery, 0m),
            next?.Label ?? "Nivel máximo",
            next?.Bonus,
            progress);
    }

    public PromiseComplianceSummary PersonalPromiseSummary(ScopedOperations scope, int week)
    {
        var records = scope.Promises.Where(x => PromiseWeek(x) == week).ToArray();
        var active = records.Count(x => IsStatus(x.Status, "activa", "cumplida", "cumplido"));
        var open = records.Count(x => IsStatus(x.Status, "vigente"));
        var failed = records.Count(x => IsStatus(x.Status, "incumplida"));
        var stolen = records.Count(x => IsStatus(x.Status, "robada"));
        var duplicate = records.Count(x => IsStatus(x.Status, "duplicada"));
        var eligible = active + open + failed;
        var focus = failed > 0
            ? $"Tienes {failed:N0} promesa(s) incumplida(s) que requieren atención."
            : open > 0 ? $"Tienes {open:N0} promesa(s) vigente(s) en seguimiento."
            : active > 0 ? "Todas tus promesas elegibles están activas."
            : "Aún no hay promesas elegibles en este corte.";
        return new PromiseComplianceSummary(eligible == 0 ? null : (decimal)active / eligible, active, open, failed, stolen, duplicate, eligible, focus);
    }

    public IReadOnlyList<MetricCard> RecoveryCards(ScopedOperations scope, int week)
    {
        var payments = scope.Payments.Where(x => x.Week == week && x.Recovery > 0m).ToArray();
        var recovery = payments.Sum(x => x.Recovery);
        var clients = payments.Select(x => x.ClientId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count();
        var summary = PersonalPromiseSummary(scope, week);
        return [
            new("Cobranza semanal", Currency(recovery), $"{payments.Length:N0} pagos · {clients:N0} cuentas", "positive"),
            new("Cumplimiento", summary.Rate is null ? "—" : summary.Rate.Value.ToString("P0", CultureInfo.GetCultureInfo("es-MX")), $"{summary.Active:N0} activas de {summary.Eligible:N0} elegibles", summary.Eligible == 0 ? "neutral" : "accent"),
            new("Promesas vigentes", $"{summary.Open:N0}", "En seguimiento esta semana", "warning"),
            new("Incumplidas", $"{summary.Failed:N0}", "Requieren atención", "danger"),
        ];
    }

    public IReadOnlyList<PromiseRecord> FilterPromises(
        ScopedOperations scope, string? id, string? client, string? agent, string? type,
        string? status, DateOnly? promiseStart, DateOnly? promiseEnd, DateOnly? createdStart, DateOnly? createdEnd,
        int? week = null)
    {
        IEnumerable<PromiseRecord> query = scope.Promises;
        if (week is not null) query = query.Where(x => PromiseWeek(x) == week);
        if (!string.IsNullOrWhiteSpace(id)) query = query.Where(x => x.Id.Contains(id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(client)) query = query.Where(x => x.ClientId.Contains(client.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(agent)) query = query.Where(x => x.Agent.Equals(agent, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        if (promiseStart is not null) query = query.Where(x => x.PromiseDate is not null && x.PromiseDate >= promiseStart);
        if (promiseEnd is not null) query = query.Where(x => x.PromiseDate is not null && x.PromiseDate <= promiseEnd);
        if (createdStart is not null) query = query.Where(x => x.CreatedOn is not null && x.CreatedOn >= createdStart);
        if (createdEnd is not null) query = query.Where(x => x.CreatedOn is not null && x.CreatedOn <= createdEnd);
        return query.OrderByDescending(x => x.PromiseDate).ThenByDescending(x => x.CreatedOn).Take(600).ToArray();
    }

    public ChartModel DailyRecoveryChart(ScopedOperations scope, int week, bool personal)
    {
        var payments = scope.Payments.Where(x => x.Week == week && x.Recovery > 0m).ToArray();
        var values = Days.Select(day => payments.Where(x => x.Day.Equals(day, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Recovery)).ToArray();
        var counts = Days.Select(day => payments.Count(x => x.Day.Equals(day, StringComparison.OrdinalIgnoreCase))).ToArray();
        var colors = values.Select(RecoveryColor).ToArray();
        return new ChartModel(
        [new
        {
            type = "bar", x = Days, y = values, text = values.Select(value => value == 0 ? "" : MoneyLabel(value)).ToArray(),
            textposition = "outside", cliponaxis = false, textfont = new { color = "#111827", size = 12 },
            marker = new { color = colors, line = new { color = "rgba(255,255,255,.62)", width = 1 }, cornerradius = 8 },
            customdata = counts.Select(x => new[] { x }).ToArray(),
            hovertemplate = "<b>%{x}</b><br><b>Recuperación:</b> $%{y:,.2f}<br><b>Pagos:</b> %{customdata[0]}<extra></extra>",
        }],
        BaseLayout(
            personal ? $"Mi recuperación por día · Semana {week}<br><sup>{Currency(values.Sum())} registrados en la semana. Selecciona una barra para ver los pagos.</sup>" : $"Cobranza diaria · Semana {week}",
            "Recuperación", 390, 88),
        Config());
    }

    public ChartModel PromiseStatusChart(ScopedOperations scope, int week)
    {
        var statuses = new[]
        {
            (Key: "vigente", Label: "Vigentes", Color: "rgba(68, 139, 232, .93)", Border: "rgba(142, 190, 255, .92)"),
            (Key: "activa", Label: "Activas", Color: "rgba(45, 163, 119, .93)", Border: "rgba(143, 226, 193, .92)"),
            (Key: "incumplida", Label: "Incumplidas", Color: "rgba(206, 78, 92, .93)", Border: "rgba(255, 174, 183, .92)"),
            (Key: "robada", Label: "Robadas", Color: "rgba(218, 135, 55, .93)", Border: "rgba(255, 199, 128, .92)"),
            (Key: "duplicada", Label: "Duplicadas", Color: "rgba(124, 87, 194, .93)", Border: "rgba(190, 166, 244, .92)"),
        };
        var series = statuses.Select(status => (object)new
        {
            type = "bar", name = status.Label, x = Days,
            y = Days.Select(day => scope.Promises.Count(x => PromiseWeek(x) == week && DayName(x.PromiseDate) == day && IsChartStatus(x.Status, status.Key))).ToArray(),
            text = Days.Select(day => scope.Promises.Count(x => PromiseWeek(x) == week && DayName(x.PromiseDate) == day && IsChartStatus(x.Status, status.Key))).Select(x => x == 0 ? "" : x.ToString(CultureInfo.InvariantCulture)).ToArray(),
            texttemplate = "%{text}", textposition = "inside", insidetextanchor = "middle", textfont = new { color = "#ffffff", size = 12 },
            marker = new { color = status.Color, line = new { color = status.Border, width = 1 }, cornerradius = 6 },
            hovertemplate = $"<b>%{{x}}</b><br><b>{status.Label}:</b> %{{y}}<extra></extra>",
        }).ToArray();
        return new ChartModel(series, BaseLayout($"Mis promesas por día · Semana {week}<br><sup>Selecciona un segmento para revisar las promesas que lo componen.</sup>", "Promesas", 430, 88, "stack"), Config());
    }

    /// <summary>Devuelve las promesas del mismo día y estatus de una barra personal.</summary>
    public IReadOnlyList<PromiseRecord> PromiseChartDetail(
        ScopedOperations scope, int week, string? day, string? chartStatus)
    {
        if (!Days.Contains(day ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(chartStatus)) return [];

        return scope.Promises
            .Where(x => PromiseWeek(x) == week
                && DayName(x.PromiseDate).Equals(day, StringComparison.OrdinalIgnoreCase)
                && IsChartStatus(x.Status, chartStatus))
            .OrderByDescending(x => x.PromiseDate)
            .ThenByDescending(x => x.CreatedOn)
            .Take(600)
            .ToArray();
    }

    public static string? PromiseChartStatusKey(string? traceName) =>
        TextNormalization.Normalize(traceName) switch
        {
            "vigentes" => "vigente",
            "activas" => "activa",
            "incumplidas" => "incumplida",
            "robadas" => "robada",
            "duplicadas" => "duplicada",
            _ => null,
        };

    public static string PromiseChartStatusLabel(string? chartStatus) => chartStatus switch
    {
        "vigente" => "vigentes",
        "activa" => "activas",
        "incumplida" => "incumplidas",
        "robada" => "robadas",
        "duplicada" => "duplicadas",
        _ => "seleccionadas",
    };

    public ChartModel TeamPromiseComplianceChart(ScopedOperations scope, int week, string? selectedAgent = null)
    {
        var records = scope.Promises.Where(x => PromiseWeek(x) == week && !string.IsNullOrWhiteSpace(x.Agent)
            && IsStatus(x.Status, "vigente", "activa", "cumplida", "cumplido", "incumplida"));
        if (!string.IsNullOrWhiteSpace(selectedAgent)) records = records.Where(x => x.Agent.Equals(selectedAgent, StringComparison.OrdinalIgnoreCase));
        var rows = records.GroupBy(x => x.Agent).Select(group =>
        {
            var daily = Days.Select(day => group.Where(x => DayName(x.PromiseDate) == day).ToArray()).ToArray();
            var dayRates = daily.Select(CalculateCompliance).ToArray();
            var all = group.ToArray();
            var weeklyRate = CalculateCompliance(all);
            // Plotly interpreta un null como celda sin dato. Es preferible a NaN,
            // que puede romper la serialización JSON de un componente interactivo.
            return new { Agent = group.Key, Rates = dayRates.Append(weeklyRate).ToArray(), Labels = dayRates.Append(weeklyRate).Select(Percent).ToArray(), Details = daily.Select(ComplianceDetail).Append(ComplianceDetail(all)).ToArray(), SortRate = weeklyRate ?? -1d, Eligible = all.Length };
        }).OrderByDescending(x => x.SortRate).ThenByDescending(x => x.Eligible).ThenBy(x => x.Agent, StringComparer.OrdinalIgnoreCase).ToArray();

        if (rows.Length == 0) return EmptyChart($"Cumplimiento de promesas · Semana {week}", "No hay promesas elegibles para esta semana.");
        return new ChartModel(
        [new
        {
            type = "heatmap", z = rows.Select(x => x.Rates).ToArray(), x = Days.Concat(["Prom. semanal"]).ToArray(), y = rows.Select(x => x.Agent).ToArray(),
            customdata = rows.Select(x => x.Details).ToArray(), text = rows.Select(x => x.Labels).ToArray(), texttemplate = "%{text}", textfont = new { size = 12 },
            colorscale = new object[][] { [0d, "rgba(244,249,255,.45)"], [.2d, "rgba(184,215,240,.58)"], [.48d, "rgba(105,171,217,.72)"], [.74d, "rgba(43,123,191,.88)"], [1d, "rgba(12,62,133,.98)"] },
            zmin = 0d, zmax = 1d, xgap = 3, ygap = 3, hoverongaps = false, colorbar = new { title = "Cumplimiento", tickformat = ".0%", thickness = 12 },
            hovertemplate = "<b>Agente:</b> %{y}<br><b>Periodo:</b> %{x}<br><b>Cumplimiento:</b> %{z:.1%}<br>Activas: %{customdata[0]}<br>Vigentes: %{customdata[1]}<br>Incumplidas: %{customdata[2]}<br>Total elegible: %{customdata[3]}<extra></extra>",
        }],
        HeatmapLayout($"Cumplimiento de promesas · Semana {week}<br><sup>Activas ÷ (Vigentes + Activas + Incumplidas). Robadas y duplicadas excluidas.</sup>", rows.Length), Config());
    }

    public ChartModel TeamDailyRecoveryChart(ScopedOperations scope, int week, string? selectedAgent = null) => RecoveryMatrix(scope, week, Days, "Día", "Recuperación diaria por agente", selectedAgent);

    public ChartModel TeamProductRecoveryChart(ScopedOperations scope, int week, string? selectedAgent = null)
    {
        var products = scope.Payments.Where(x => x.Week == week && !string.IsNullOrWhiteSpace(x.Product)).Select(x => x.Product).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return RecoveryMatrix(scope, week, products, "Producto", "Recuperación por agente y producto", selectedAgent);
    }

    public ChartModel TeamWeeklyRecoveryChart(ScopedOperations scope, IReadOnlyList<int>? selectedWeeks, string? selectedAgent = null)
    {
        var weeks = (selectedWeeks is { Count: > 0 } ? selectedWeeks : scope.Weeks).Distinct().Order().ToArray();
        var payments = scope.Payments.Where(x => weeks.Contains(x.Week ?? -1));
        if (!string.IsNullOrWhiteSpace(selectedAgent)) payments = payments.Where(x => x.Agent.Equals(selectedAgent, StringComparison.OrdinalIgnoreCase));
        var rows = payments.GroupBy(x => string.IsNullOrWhiteSpace(x.Agent) ? "Sin agente" : x.Agent).Select(group => new { Agent = group.Key, Values = weeks.Select(week => group.Where(x => x.Week == week).Sum(x => x.Recovery)).ToArray() }).OrderByDescending(x => x.Values.Sum()).ThenBy(x => x.Agent, StringComparer.OrdinalIgnoreCase).ToArray();
        if (rows.Length == 0) return EmptyChart("Comparativo semanal por agente", "No hay cobranza de agentes para comparar.");
        var values = rows.Select(x => x.Values.Select(value => (double)value).ToArray()).ToArray();
        return new ChartModel(
        [new { type = "heatmap", z = values, x = weeks.Select(x => $"Semana {x}").ToArray(), y = rows.Select(x => x.Agent).ToArray(), text = rows.Select(x => x.Values.Select(MoneyLabel).ToArray()).ToArray(), texttemplate = "%{text}", colorscale = "Blues", xgap = 4, ygap = 4, zmin = 0d, zmax = Math.Max(values.SelectMany(x => x).DefaultIfEmpty(0d).Max(), 1d), colorbar = new { title = "Cobranza", thickness = 10 }, hovertemplate = "<b>Agente:</b> %{y}<br><b>%{x}</b><br>Cobranza: $%{z:,.2f}<extra></extra>" }],
        HeatmapLayout("Comparativo semanal por agente", rows.Length), Config());
    }

    private ChartModel RecoveryMatrix(ScopedOperations scope, int week, IReadOnlyList<string> dimensions, string dimensionLabel, string title, string? selectedAgent)
    {
        var payments = scope.Payments.Where(x => x.Week == week);
        if (!string.IsNullOrWhiteSpace(selectedAgent)) payments = payments.Where(x => x.Agent.Equals(selectedAgent, StringComparison.OrdinalIgnoreCase));
        var rows = payments.GroupBy(x => string.IsNullOrWhiteSpace(x.Agent) ? "Sin agente" : x.Agent).Select(group => new { Agent = group.Key, Values = dimensions.Select(dimension => group.Where(x => (dimensionLabel == "Día" ? x.Day : x.Product).Equals(dimension, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Recovery)).ToArray() }).OrderByDescending(x => x.Values.Sum()).ThenBy(x => x.Agent, StringComparer.OrdinalIgnoreCase).ToArray();
        if (rows.Length == 0) return EmptyChart($"{title} · Semana {week}", $"No hay información para la semana {week}.");

        var displayValues = rows.Select(x => x.Values.Append(x.Values.Sum()).ToArray()).ToArray();
        var colorValues = rows.Select(x => x.Values.Select(value => (double?)value).Append(null).ToArray()).ToArray();
        var allValues = rows.SelectMany(x => x.Values).Select(x => (double)x).ToArray();
        return new ChartModel(
        [new { type = "heatmap", z = colorValues, x = dimensions.Append("Total").ToArray(), y = rows.Select(x => x.Agent).ToArray(), text = displayValues.Select(row => row.Select(MoneyLabel).ToArray()).ToArray(), texttemplate = "%{text}", customdata = displayValues, colorscale = "Blues", xgap = 2, ygap = 2, zmin = 0d, zmax = Math.Max(allValues.DefaultIfEmpty(0d).Max(), 1d), colorbar = new { title = "Cobranza", thickness = 10 }, hovertemplate = $"<b>Agente:</b> %{{y}}<br><b>{dimensionLabel}:</b> %{{x}}<br>Cobranza: $%{{customdata:,.2f}}<extra></extra>" }],
        HeatmapLayout($"{title} · Semana {week}", rows.Length), Config());
    }

    private static ScopedOperations BuildScope(IReadOnlyList<PaymentRecord> payments, IReadOnlyList<PromiseRecord> promises, IReadOnlyList<PortfolioRecord> portfolio)
    {
        var weeks = payments.Select(x => x.Week).Concat(promises.Select(PromiseWeek)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().Order().ToArray();
        return new ScopedOperations(payments, promises, portfolio, weeks);
    }

    public static int? PromiseWeek(PromiseRecord promise) => promise.PromiseDate is null ? null : ISOWeek.GetWeekOfYear(promise.PromiseDate.Value.ToDateTime(TimeOnly.MinValue));
    public static string DayName(DateOnly? date) => date?.DayOfWeek switch { DayOfWeek.Monday => "Lunes", DayOfWeek.Tuesday => "Martes", DayOfWeek.Wednesday => "Miércoles", DayOfWeek.Thursday => "Jueves", DayOfWeek.Friday => "Viernes", DayOfWeek.Saturday => "Sábado", DayOfWeek.Sunday => "Domingo", _ => string.Empty };
    public static string Currency(decimal amount) => amount.ToString("C2", CultureInfo.GetCultureInfo("es-MX"));
    public static bool IsStatus(string source, params string[] candidates) => candidates.Contains(TextNormalization.Normalize(source), StringComparer.OrdinalIgnoreCase);

    private static bool IsChartStatus(string source, string key) => key.Equals("activa", StringComparison.OrdinalIgnoreCase)
        ? IsStatus(source, "activa", "cumplida", "cumplido")
        : IsStatus(source, key);

    private static object[] ComplianceDetail(IEnumerable<PromiseRecord> records)
    {
        var list = records.ToArray();
        var active = list.Count(x => IsStatus(x.Status, "activa", "cumplida", "cumplido"));
        var open = list.Count(x => IsStatus(x.Status, "vigente"));
        var failed = list.Count(x => IsStatus(x.Status, "incumplida"));
        return [active, open, failed, active + open + failed];
    }
    private static double? CalculateCompliance(IEnumerable<PromiseRecord> promises)
    {
        var records = promises.ToArray();
        if (records.Length == 0) return null;
        var active = records.Count(x => IsStatus(x.Status, "activa", "cumplida", "cumplido"));
        return (double)active / records.Length;
    }
    private static string Percent(double? value) => value is null ? "—" : value.Value.ToString("P0", CultureInfo.GetCultureInfo("es-MX"));
    private static string MoneyLabel(decimal value) => value == 0 ? string.Empty : value.ToString("C0", CultureInfo.GetCultureInfo("es-MX"));
    private static string RecoveryColor(decimal value) => value >= 5_000m ? "#1f9b72" : value >= 4_000m ? "#c38d25" : value > 0m ? "#c74857" : "rgba(124,145,169,.48)";
    private static object Config() => new { responsive = true, displayModeBar = false };
    private static ChartModel EmptyChart(string title, string message) => new([new { type = "scatter", x = Array.Empty<string>(), y = Array.Empty<string>() }], new { title = new { text = title, x = .02 }, annotations = new[] { new { text = message, showarrow = false, x = .5, y = .5, xref = "paper", yref = "paper" } }, height = 360, paper_bgcolor = "rgba(0,0,0,0)", plot_bgcolor = "rgba(0,0,0,0)" }, Config());
    private static object HeatmapLayout(string title, int rowCount) => new
    {
        title = new { text = title, x = .015, font = new { size = 17, color = "#111827" } }, height = Math.Max(510, 180 + rowCount * 38), margin = new { l = 230, r = 85, t = 92, b = 62 }, paper_bgcolor = "rgba(0,0,0,0)", plot_bgcolor = "rgba(0,0,0,0)", font = new { family = "-apple-system, BlinkMacSystemFont, 'SF Pro Text', sans-serif", color = "#111827" }, xaxis = new { side = "top", fixedrange = true, gridcolor = "rgba(47,68,94,.22)" }, yaxis = new { autorange = "reversed", fixedrange = true, tickfont = new { size = 12, color = "#0b1220" }, gridcolor = "rgba(47,68,94,.22)" },
    };
    private static object BaseLayout(string title, string yAxis, int height, int topMargin, string? barmode = null) => new
    {
        title = new { text = title, x = .015, font = new { size = 17, color = "#111827" } }, height, margin = new { l = 66, r = 46, t = topMargin, b = 60 }, paper_bgcolor = "rgba(0,0,0,0)", plot_bgcolor = "rgba(0,0,0,0)", font = new { family = "-apple-system, BlinkMacSystemFont, 'SF Pro Text', sans-serif", color = "#111827" }, xaxis = new { fixedrange = true, gridcolor = "rgba(47,68,94,.16)", tickfont = new { color = "#111827", size = 12 } }, yaxis = new { title = yAxis, tickprefix = yAxis == "Recuperación" || yAxis == "Cobranza" ? "$" : "", tickformat = yAxis == "Promesas" ? ",d" : ",.0f", rangemode = "tozero", fixedrange = true, gridcolor = "rgba(47,68,94,.16)", tickfont = new { color = "#111827", size = 12 } }, legend = new { orientation = "h", yanchor = "bottom", y = 1.02, xanchor = "right", x = 1d, font = new { size = 12 } }, barmode,
    };
}
