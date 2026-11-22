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

    // El servicio vive durante la sesión del usuario. Conservamos los alcances
    // ya calculados mientras el corte no cambie para no volver a recorrer todos
    // los pagos y promesas al actualizar un filtro o una gráfica.
    private string? _cachedSnapshotVersion;
    private string? _scopeCacheKey;
    private ScopedOperations? _scopeCache;
    private string? _teamScopeCacheKey;
    private ScopedOperations? _teamScopeCache;
    private readonly Dictionary<(int Week, int TopCount), IReadOnlyList<WeeklyAgentRecoveryRank>> _rankingCache = [];

    public AgentDataSnapshot Snapshot => snapshots.Current;

    public ScopedOperations Scope()
    {
        var snapshot = Snapshot;
        EnsureSnapshotCaches(snapshot.Version);
        var user = session.User;
        if (user is null) return new ScopedOperations([], [], [], []);

        var cacheKey = $"{user.Id}\u001f{user.Username}\u001f{user.DisplayName}";
        if (_scopeCache is not null && string.Equals(_scopeCacheKey, cacheKey, StringComparison.Ordinal))
        {
            return _scopeCache;
        }

        var aliases = snapshot.AgentDirectory
            .Where(directory => TextNormalization.AgentId(directory.Id)
                .Equals(user.Id, StringComparison.OrdinalIgnoreCase))
            .Select(directory => $"{directory.FirstName} {directory.PaternalSurname}".Trim())
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .ToArray();

        bool Owns(string sourceAgent, string sourceId) =>
            (!string.IsNullOrWhiteSpace(sourceId) && sourceId == user.Id)
            || (string.IsNullOrWhiteSpace(sourceId)
                && (TextNormalization.AgentNameMatches(sourceAgent, user.DisplayName)
                    || aliases.Any(alias => TextNormalization.AgentNameMatches(sourceAgent, alias))));

        var payments = snapshot.Payments.Where(x => Owns(x.Agent, x.AgentId)).ToArray();
        var promises = snapshot.Promises.Where(x => Owns(x.Agent, x.AgentId)).ToArray();
        var allowedClients = payments.Select(x => x.ClientId).Concat(promises.Select(x => x.ClientId))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var portfolio = allowedClients
            .Select(clientId => snapshot.PortfolioByClientId.TryGetValue(clientId, out var record) ? record : null)
            .OfType<PortfolioRecord>()
            .ToArray();
        _scopeCacheKey = cacheKey;
        return _scopeCache = BuildScope(payments, promises, portfolio);
    }

    /// <summary>
    /// Alcance completo del equipo para la consola de Gerencia y Supervisión.
    /// La página que lo consume exige uno de esos roles; un agente nunca recibe
    /// este alcance aunque intente abrir la ruta de forma directa.
    /// </summary>
    public ScopedOperations TeamScope()
    {
        if (!session.HasFullAccess) return new ScopedOperations([], [], [], []);

        var snapshot = Snapshot;
        EnsureSnapshotCaches(snapshot.Version);
        var cacheKey = session.IsSupervisor ? "supervisor" : "manager";
        if (_teamScopeCache is not null && string.Equals(_teamScopeCacheKey, cacheKey, StringComparison.Ordinal))
        {
            return _teamScopeCache;
        }

        // El orgánico no pertenece a la cartera de un agente. Supervisión
        // trabaja sólo con la cobranza asignada; Gerencia conserva la vista
        // íntegra para sus revisiones operativas y exportaciones.
        var payments = session.IsSupervisor
            ? snapshot.Payments
            : snapshot.Payments.Concat(snapshot.OrganicPayments).ToArray();

        _teamScopeCacheKey = cacheKey;
        return _teamScopeCache = BuildScope(payments, snapshot.Promises, snapshot.Portfolio);
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

    /// <summary>Etiqueta operativa para interfaz; el valor original de Excel se conserva para filtros y exportaciones.</summary>
    public static string PromiseStatusLabel(string? status) => TextNormalization.Normalize(status) switch
    {
        "duplicada" => "Duplicada",
        "robada" => "Robada",
        "activa" or "cumplida" or "cumplido" => "Pagada",
        "incumplida" => "No pagó",
        "vigente" or "pendiente de pago" or "esperando pago" => "Esperando pago",
        _ => string.IsNullOrWhiteSpace(status) ? "Sin estatus" : status.Trim(),
    };

    public ScopedOperations FilterAgentStatus(ScopedOperations scope, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return scope;
        var activeByAgent = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool IsActive(string agent)
        {
            if (activeByAgent.TryGetValue(agent, out var isActive)) return isActive;
            isActive = Snapshot.AgentDirectory.Any(directory =>
                TextNormalization.AgentNameMatches(agent, $"{directory.FirstName} {directory.PaternalSurname}"));
            activeByAgent[agent] = isActive;
            return isActive;
        }
        var wantActive = status.Equals("Activo", StringComparison.OrdinalIgnoreCase);
        var payments = scope.Payments.Where(x => IsActive(x.Agent) == wantActive).ToArray();
        var promises = scope.Promises.Where(x => IsActive(x.Agent) == wantActive).ToArray();
        return BuildScope(payments, promises, scope.Portfolio);
    }

    /// <summary>
    /// Consulta referencias y agrega un resumen estrictamente limitado de la
    /// última promesa del cliente. El detalle de promesas continúa restringido
    /// al agente propietario en las pantallas operativas.
    /// </summary>
    public IReadOnlyList<ReferenceLookupResult> FindContacts(string query, bool byPhone)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var snapshot = Snapshot;
        IReadOnlyList<ContactRecord> contacts;
        if (byPhone)
        {
            var phone = TextNormalization.Phone(query);
            if (!snapshot.ContactsByPhone.TryGetValue(phone, out var phoneContacts)) return [];
            contacts = phoneContacts
                .Select(contact => contact.ClientId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(clientId => snapshot.ContactsByClientId.TryGetValue(clientId, out var clientContacts)
                    ? clientContacts
                    : [])
                .OrderBy(contact => contact.ClientId)
                .ToArray();
        }
        else
        {
            contacts = snapshot.ContactsByClientId.TryGetValue(query.Trim(), out var clientContacts)
                ? clientContacts
                : [];
        }

        return contacts.Select(contact =>
        {
            snapshot.LatestPromiseByClientId.TryGetValue(contact.ClientId, out var promise);
            return new ReferenceLookupResult(
                contact,
                promise?.Agent ?? string.Empty,
                promise?.Status ?? string.Empty);
        }).ToArray();
    }

    public IReadOnlyList<ContactRecord> FindWhatsAppCandidates(string phone)
    {
        var normalizedPhone = TextNormalization.Phone(phone);
        if (!Snapshot.ContactsByPhone.TryGetValue(normalizedPhone, out var contacts)) return [];
        return contacts
        .GroupBy(x => x.ClientId, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(x => x.Status.Equals("Activo", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.ClientName)).First())
        .OrderBy(x => x.ClientName).ToArray();
    }

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

    /// <summary>
    /// Ranking compacto de cobranza del equipo activo. Se lee del corte completo
    /// para comparar al equipo, pero entrega exclusivamente el total semanal
    /// agregado por agente; nunca se reutiliza para abrir pagos ajenos.
    /// </summary>
    public IReadOnlyList<WeeklyAgentRecoveryRank> WeeklyAgentRecoveryRanking(int week, int topCount = 4)
    {
        if (week <= 0) return [];

        var snapshot = Snapshot;
        EnsureSnapshotCaches(snapshot.Version);
        var cacheKey = (week, Math.Max(1, topCount));
        if (_rankingCache.TryGetValue(cacheKey, out var cached)) return cached;

        var roster = ActiveRankingRoster();
        if (roster.Count == 0) return [];

        var payments = snapshot.Payments
            .Where(payment => payment.Week == week && payment.Recovery > 0m)
            .ToArray();

        var ranked = roster
            .Select(agent => new
            {
                agent.DisplayName,
                agent.IsCurrentUser,
                Recovery = payments
                    .Where(payment => PaymentBelongsTo(payment, agent))
                    .Sum(payment => payment.Recovery),
            })
            // Los agentes sin cobranza no ocupan espacio, salvo la persona que
            // abrió la pantalla: siempre debe poder reconocer su posición.
            .Where(agent => agent.Recovery > 0m || agent.IsCurrentUser)
            .OrderByDescending(agent => agent.Recovery)
            .ThenBy(agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select((agent, index) => new WeeklyAgentRecoveryRank(
                index + 1,
                agent.DisplayName,
                agent.Recovery,
                agent.IsCurrentUser))
            .ToArray();

        if (ranked.Length == 0) return [];

        var visible = ranked.Take(Math.Max(1, topCount)).ToList();
        var current = ranked.FirstOrDefault(agent => agent.IsCurrentUser);
        if (current is not null && !visible.Any(agent => agent.IsCurrentUser))
        {
            // Conserva el Top 4 y agrega la fila personal al final si quedó fuera.
            visible.Add(current);
        }
        return _rankingCache[cacheKey] = visible;
    }

    private void EnsureSnapshotCaches(string version)
    {
        if (string.Equals(_cachedSnapshotVersion, version, StringComparison.Ordinal)) return;
        _cachedSnapshotVersion = version;
        _scopeCacheKey = null;
        _scopeCache = null;
        _teamScopeCacheKey = null;
        _teamScopeCache = null;
        _rankingCache.Clear();
    }

    private IReadOnlyList<RankingAgent> ActiveRankingRoster()
    {
        var currentUser = session.User;
        var agents = new Dictionary<string, RankingAgent>(StringComparer.OrdinalIgnoreCase);

        // agentes_activos.xlsx es el padrón operativo preferido para no incluir
        // gestiones históricas de personas que ya no están activas.
        foreach (var directory in Snapshot.AgentDirectory)
        {
            var id = TextNormalization.AgentId(directory.Id);
            var fullName = $"{directory.FirstName} {directory.PaternalSurname}".Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fullName)) continue;
            agents.TryAdd(id, new RankingAgent(
                id,
                fullName,
                CompactAgentName(directory.FirstName, directory.PaternalSurname),
                IsCurrentAgent(id, fullName, currentUser)));
        }

        // Si el archivo de agentes activos está pendiente, el padrón de acceso
        // conserva el ranking funcional con los agentes habilitados.
        if (agents.Count == 0)
        {
            foreach (var user in Snapshot.Users.Where(user => user.IsActive && user.IsAgent))
            {
                if (string.IsNullOrWhiteSpace(user.Id) || string.IsNullOrWhiteSpace(user.DisplayName)) continue;
                agents.TryAdd(user.Id, new RankingAgent(
                    user.Id,
                    user.DisplayName,
                    CompactAgentName(user.DisplayName),
                    IsCurrentAgent(user.Id, user.DisplayName, currentUser)));
            }
        }

        // Asegura el highlight incluso si el usuario recién se habilitó y aún no
        // aparece en el archivo de agentes activos del corte.
        if (currentUser?.IsAgent == true && !string.IsNullOrWhiteSpace(currentUser.Id) && !agents.ContainsKey(currentUser.Id))
        {
            agents[currentUser.Id] = new RankingAgent(
                currentUser.Id,
                currentUser.DisplayName,
                CompactAgentName(currentUser.DisplayName),
                true);
        }

        return agents.Values.ToArray();
    }

    private static bool PaymentBelongsTo(PaymentRecord payment, RankingAgent agent) =>
        (!string.IsNullOrWhiteSpace(payment.AgentId) &&
            string.Equals(payment.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase)) ||
        (string.IsNullOrWhiteSpace(payment.AgentId) &&
            TextNormalization.AgentNameMatches(payment.Agent, agent.MatchName));

    private static bool IsCurrentAgent(string agentId, string agentName, AgentUser? currentUser) =>
        currentUser?.IsAgent == true &&
        (string.Equals(agentId, currentUser.Id, StringComparison.OrdinalIgnoreCase) ||
            TextNormalization.AgentNameMatches(agentName, currentUser.DisplayName));

    private static string CompactAgentName(string value)
    {
        var tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "Agente";

        var culture = CultureInfo.GetCultureInfo("es-MX");
        var firstName = culture.TextInfo.ToTitleCase(tokens[0].ToLower(culture));
        return tokens.Length == 1
            ? firstName
            : $"{firstName} {char.ToUpper(tokens[^1][0], culture)}.";
    }

    private static string CompactAgentName(string firstName, string paternalSurname)
    {
        var nameTokens = firstName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var surname = paternalSurname.Trim().FirstOrDefault(char.IsLetter);
        if (nameTokens.Length == 0) return "Agente";

        var culture = CultureInfo.GetCultureInfo("es-MX");
        var displayName = culture.TextInfo.ToTitleCase(nameTokens[0].ToLower(culture));
        return surname == default
            ? displayName
            : $"{displayName} {char.ToUpper(surname, culture)}.";
    }

    private sealed record RankingAgent(string Id, string MatchName, string DisplayName, bool IsCurrentUser);

    public PromiseComplianceSummary PersonalPromiseSummary(ScopedOperations scope, int week)
        => PersonalPromiseSummary(scope.Promises, week);

    public PromiseComplianceSummary PersonalPromiseSummary(IEnumerable<PromiseRecord> promises, int week)
    {
        var records = promises.Where(x => PromiseWeek(x) == week).ToArray();
        var active = records.Count(x => IsStatus(x.Status, "activa", "cumplida", "cumplido"));
        var open = records.Count(x => IsStatus(x.Status, "vigente"));
        var failed = records.Count(x => IsStatus(x.Status, "incumplida"));
        var stolen = records.Count(x => IsStatus(x.Status, "robada"));
        var duplicate = records.Count(x => IsStatus(x.Status, "duplicada"));
        var eligible = active + open + failed;
        var focus = failed > 0
            ? $"Tienes {failed:N0} promesa(s) sin pago que requieren atención."
            : open > 0 ? $"Tienes {open:N0} promesa(s) esperando pago en seguimiento."
            : active > 0 ? "Todas tus promesas elegibles están pagadas."
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
            new("Cumplimiento", summary.Rate is null ? "—" : summary.Rate.Value.ToString("P0", CultureInfo.GetCultureInfo("es-MX")), $"{summary.Active:N0} pagadas de {summary.Eligible:N0} elegibles", summary.Eligible == 0 ? "neutral" : "accent"),
            new("Esperando pago", $"{summary.Open:N0}", "En seguimiento esta semana", "warning"),
            new("No pagó", $"{summary.Failed:N0}", "Requieren atención", "danger"),
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

    /// <summary>Filtra los grupos de estatus que se muestran en el resumen personal de promesas.</summary>
    public static IEnumerable<PromiseRecord> FilterPromiseStatusGroup(IEnumerable<PromiseRecord> records, string? group)
    {
        return TextNormalization.Normalize(group) switch
        {
            "activa" => records.Where(record => IsChartStatus(record.Status, "activa")),
            "vigente" => records.Where(record => IsChartStatus(record.Status, "vigente")),
            "incumplida" => records.Where(record => IsChartStatus(record.Status, "incumplida")),
            "robada" => records.Where(record => IsChartStatus(record.Status, "robada")),
            "duplicada" => records.Where(record => IsChartStatus(record.Status, "duplicada")),
            "elegible" => records.Where(record => IsChartStatus(record.Status, "activa")
                || IsChartStatus(record.Status, "vigente")
                || IsChartStatus(record.Status, "incumplida")),
            _ => records,
        };
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
            "Recuperación", 338, 74),
        Config());
    }

    public ChartModel PromiseStatusChart(ScopedOperations scope, int week)
        => PromiseStatusChart(scope.Promises, week);

    public ChartModel PromiseStatusChart(IEnumerable<PromiseRecord> promises, int week)
    {
        var records = promises.ToArray();
        var statuses = new[]
        {
            (Key: "vigente", Label: PromiseStatusLabel("vigente"), Color: "rgba(68, 139, 232, .93)", Border: "rgba(142, 190, 255, .92)"),
            (Key: "activa", Label: PromiseStatusLabel("activa"), Color: "rgba(45, 163, 119, .93)", Border: "rgba(143, 226, 193, .92)"),
            (Key: "incumplida", Label: PromiseStatusLabel("incumplida"), Color: "rgba(206, 78, 92, .93)", Border: "rgba(255, 174, 183, .92)"),
            (Key: "robada", Label: PromiseStatusLabel("robada"), Color: "rgba(218, 135, 55, .93)", Border: "rgba(255, 199, 128, .92)"),
            (Key: "duplicada", Label: PromiseStatusLabel("duplicada"), Color: "rgba(124, 87, 194, .93)", Border: "rgba(190, 166, 244, .92)"),
        };
        var series = statuses.Select(status => (object)new
        {
            type = "bar", name = status.Label, x = Days,
            y = Days.Select(day => records.Count(x => PromiseWeek(x) == week && DayName(x.PromiseDate) == day && IsChartStatus(x.Status, status.Key))).ToArray(),
            text = Days.Select(day => records.Count(x => PromiseWeek(x) == week && DayName(x.PromiseDate) == day && IsChartStatus(x.Status, status.Key))).Select(x => x == 0 ? "" : x.ToString(CultureInfo.InvariantCulture)).ToArray(),
            texttemplate = "%{text}", textposition = "inside", insidetextanchor = "middle", textfont = new { color = "#ffffff", size = 12 },
            marker = new { color = status.Color, line = new { color = status.Border, width = 1 }, cornerradius = 6 },
            hovertemplate = $"<b>%{{x}}</b><br><b>{status.Label}:</b> %{{y}}<extra></extra>",
        }).ToArray();
        return new ChartModel(series, BaseLayout($"Mis promesas por día · Semana {week}<br><sup>Selecciona un segmento para revisar las promesas que lo componen.</sup>", "Promesas", 364, 74, "stack"), Config());
    }

    /// <summary>Devuelve las promesas del mismo día y estatus de una barra personal.</summary>
    public IReadOnlyList<PromiseRecord> PromiseChartDetail(
        ScopedOperations scope, int week, string? day, string? chartStatus)
        => PromiseChartDetail(scope.Promises, week, day, chartStatus);

    public IReadOnlyList<PromiseRecord> PromiseChartDetail(
        IEnumerable<PromiseRecord> promises, int week, string? day, string? chartStatus)
    {
        if (!Days.Contains(day ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(chartStatus)) return [];

        return promises
            .Where(x => PromiseWeek(x) == week
                && DayName(x.PromiseDate).Equals(day, StringComparison.OrdinalIgnoreCase)
                && IsChartStatus(x.Status, chartStatus))
            .OrderByDescending(x => x.PromiseDate)
            .ThenByDescending(x => x.CreatedOn)
            .Take(600)
            .ToArray();
    }

    /// <summary>Pagos descargables de la semana seleccionada para el usuario autenticado.</summary>
    public IReadOnlyList<PaymentRecord> WeeklyPaymentExport(ScopedOperations scope, int week) => scope.Payments
        .Where(x => x.Week == week && x.Recovery > 0m)
        .OrderBy(x => x.ReceivedOn)
        .ThenBy(x => x.ClientId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>
    /// Promesas descargables de la selección actual. El scope recibido es
    /// personal, así que nunca incorpora promesas de otros gestores.
    /// </summary>
    public IReadOnlyList<PromiseRecord> PromiseExport(
        ScopedOperations scope, int week, string? status, string? metric, string? day, string? chartStatus)
    {
        IEnumerable<PromiseRecord> records = scope.Promises.Where(x => PromiseWeek(x) == week);
        if (!string.IsNullOrWhiteSpace(status))
            records = records.Where(x => x.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        records = FilterPromiseStatusGroup(records, metric);
        if (Days.Contains(day ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(chartStatus))
        {
            records = records.Where(x => DayName(x.PromiseDate).Equals(day, StringComparison.OrdinalIgnoreCase)
                && IsChartStatus(x.Status, chartStatus));
        }
        return records
            .OrderByDescending(x => x.PromiseDate)
            .ThenByDescending(x => x.CreatedOn)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? PromiseChartStatusKey(string? traceName) =>
        TextNormalization.Normalize(traceName) switch
        {
            "esperando pago" or "vigentes" => "vigente",
            "pagada" or "activas" => "activa",
            "no pago" or "incumplidas" => "incumplida",
            "robada" or "robadas" => "robada",
            "duplicada" or "duplicadas" => "duplicada",
            _ => null,
        };

    public static string PromiseChartStatusLabel(string? chartStatus) => chartStatus switch
    {
        "vigente" => PromiseStatusLabel("vigente"),
        "activa" => PromiseStatusLabel("activa"),
        "incumplida" => PromiseStatusLabel("incumplida"),
        "robada" => PromiseStatusLabel("robada"),
        "duplicada" => PromiseStatusLabel("duplicada"),
        "elegible" => "elegibles",
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
            hovertemplate = "<b>Agente:</b> %{y}<br><b>Periodo:</b> %{x}<br><b>Cumplimiento:</b> %{z:.1%}<br>Pagadas: %{customdata[0]}<br>Esperando pago: %{customdata[1]}<br>No pagó: %{customdata[2]}<br>Total elegible: %{customdata[3]}<extra></extra>",
        }],
        HeatmapLayout($"Cumplimiento de promesas · Semana {week}<br><sup>Pagadas ÷ (Esperando pago + Pagadas + No pagó). Robada y Duplicada se excluyen.</sup>", rows.Length), Config());
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
    private static ChartModel EmptyChart(string title, string message) => new([new { type = "scatter", x = Array.Empty<string>(), y = Array.Empty<string>() }], new { title = new { text = title, x = .02 }, annotations = new[] { new { text = message, showarrow = false, x = .5, y = .5, xref = "paper", yref = "paper" } }, height = 300, paper_bgcolor = "rgba(0,0,0,0)", plot_bgcolor = "rgba(0,0,0,0)" }, Config());
    private static object HeatmapLayout(string title, int rowCount) => new
    {
        title = new { text = title, x = .015, font = new { size = 16, color = "#111827" } }, height = Math.Max(440, 156 + rowCount * 34), margin = new { l = 210, r = 76, t = 78, b = 54 }, paper_bgcolor = "rgba(0,0,0,0)", plot_bgcolor = "rgba(0,0,0,0)", font = new { family = "-apple-system, BlinkMacSystemFont, 'SF Pro Text', sans-serif", color = "#111827" }, xaxis = new { side = "top", fixedrange = true, gridcolor = "rgba(47,68,94,.22)" }, yaxis = new { autorange = "reversed", fixedrange = true, tickfont = new { size = 12, color = "#0b1220" }, gridcolor = "rgba(47,68,94,.22)" },
    };
    private static object BaseLayout(string title, string yAxis, int height, int topMargin, string? barmode = null) => new
    {
        title = new { text = title, x = .015, font = new { size = 16, color = "#111827" } }, height, margin = new { l = 60, r = 34, t = topMargin, b = 52 }, paper_bgcolor = "rgba(0,0,0,0)", plot_bgcolor = "rgba(0,0,0,0)", font = new { family = "-apple-system, BlinkMacSystemFont, 'SF Pro Text', sans-serif", color = "#111827" }, xaxis = new { fixedrange = true, gridcolor = "rgba(47,68,94,.16)", tickfont = new { color = "#111827", size = 12 } }, yaxis = new { title = yAxis, tickprefix = yAxis == "Recuperación" || yAxis == "Cobranza" ? "$" : "", tickformat = yAxis == "Promesas" ? ",d" : ",.0f", rangemode = "tozero", fixedrange = true, gridcolor = "rgba(47,68,94,.16)", tickfont = new { color = "#111827", size = 12 } }, legend = new { orientation = "h", yanchor = "bottom", y = 1.02, xanchor = "right", x = 1d, font = new { size = 11 } }, barmode,
    };
}
