using System.Globalization;
using System.Text.RegularExpressions;

namespace Ascendra.Agents.Domain;

/// <summary>
/// Entradas normalizadas del proceso de cobranza. Los identificadores de las
/// filas provienen de PostgreSQL: nunca se usa una posición de Excel como llave.
/// </summary>
public sealed record OperationalPayment(
    long SourceId, string? ClientId, DateOnly? ReceivedOn, decimal? Recovery,
    int? Week, string? Product, string? Concept, string? PaymentPlan, string? Zone);

public sealed record OperationalPromise(
    int DatabasePromiseId, string VisibleId, int? AscendraRegistrationId,
    string? ClientId, string? ClientNumber, string? Agent, string? AgentId,
    DateOnly? PromiseDate, decimal? InitialAmount, decimal? WeeklyAmount,
    string? PromiseType, int? NumberOfWeeks, DateTime? CreatedAt);

public sealed record OperationalPortfolio(
    string? ClientId, string? ClientName, string? Product, decimal? TotalBalance,
    string? Status, string? PaymentDay, string? Zone, IReadOnlyList<string?> Phones);

public sealed record OperationalReference(
    string? ClientId, string? Phone, string? ReferenceName, string? Relationship);

/// <summary>Contacto importado de una libreta histórica ya existente.</summary>
public sealed record OperationalContactSeed(
    string? ClientId, string? Phone, string? ReferenceName, string? Relationship, string? Source);

public sealed record OperationalGeography(string? ClientId, string? Zone);

public sealed record OperationalAgent(string? Id, string? Name);

public sealed record ProcessedContact(
    string ClientId, string Phone, string ReferenceName, string Relationship,
    string ClientName, string Product, decimal? TotalBalance, string Status, string Source);

public sealed record ProcessedPortfolio(
    string ClientId, string ClientName, string Product, decimal? TotalBalance,
    string Status, string PaymentDay, string Zone, IReadOnlyList<string> Phones);

public sealed record ProcessedPayment(
    long SourceId, string ClientId, DateOnly? ReceivedOn, decimal? Recovery,
    int? Week, string Product, string Concept, string PaymentPlan, string Zone,
    string Agent, string AgentId, string ClientName, string Day,
    string PromiseId, int? DatabasePromiseId, string AssignmentType);

public sealed record ProcessedPromise(
    int DatabasePromiseId, string VisibleId, int? AscendraRegistrationId,
    string ClientId, string ClientName, string ClientNumber, string Agent, string AgentId,
    DateOnly? PromiseDate, decimal? InitialAmount, decimal? WeeklyAmount,
    string PromiseType, int? NumberOfWeeks, DateTime? CreatedAt, string Status);

public sealed record OperationalProcessingResult(
    IReadOnlyList<ProcessedPortfolio> Portfolio,
    IReadOnlyList<ProcessedContact> Contacts,
    IReadOnlyList<ProcessedPayment> Payments,
    IReadOnlyList<ProcessedPromise> Promises);

/// <summary>
/// Port puro de las reglas que antes vivían en el notebook y en el generador
/// Python. No accede a archivos ni a PostgreSQL: así los cruces se prueban de
/// forma determinista y se pueden ejecutar al llegar un lote nuevo.
/// </summary>
public static class OperationalProcessingRules
{
    public static OperationalProcessingResult Process(
        IReadOnlyList<OperationalPayment> payments,
        IReadOnlyList<OperationalPromise> promises,
        IReadOnlyList<OperationalPortfolio> portfolio,
        IReadOnlyList<OperationalReference> references,
        IReadOnlyList<OperationalContactSeed> contactSeeds,
        IReadOnlyList<OperationalGeography> geography,
        IReadOnlyList<OperationalAgent> agents,
        DateOnly cutoff)
    {
        var canonicalAgents = agents
            .Select(agent => (Id: AgentId(agent.Id), Name: FormatAgent(agent.Name)))
            .Where(agent => agent.Id.Length > 0 && agent.Name.Length > 0)
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    ? group.First().Name
                    : throw new InvalidOperationException($"El padrón asigna más de un nombre al Agente_ID {group.Key}."),
                StringComparer.OrdinalIgnoreCase);

        var normalizedPromises = promises.Select((promise, position) =>
        {
            var agentId = AgentId(promise.AgentId);
            string agentName;
            if (agentId.Length > 0)
            {
                if (!canonicalAgents.TryGetValue(agentId, out var canonicalName))
                    throw new InvalidOperationException($"Agente_ID sin nombre canónico en el padrón: {agentId}.");
                agentName = canonicalName;
            }
            else agentName = FormatAgent(promise.Agent);
            var clientId = Clean(promise.ClientId);
            DateOnly? start = promise.CreatedAt is null ? null : DateOnly.FromDateTime(promise.CreatedAt.Value);
            var endExclusive = promise.PromiseDate?.AddDays(2);
            return new PromiseState(promise, position, clientId, agentName, agentId, start, endExclusive);
        }).ToArray();

        var realPayments = payments.Select((payment, position) => new PaymentState(
                payment, position, Clean(payment.ClientId)))
            .Where(payment => payment.Payment.Recovery is > 0m && payment.Payment.ReceivedOn is not null)
            .ToArray();

        // Se conoce primero si una promesa tiene algún pago positivo en su
        // propia ventana. Ese dato, no la asignación final, decide si bloquea
        // una promesa posterior, igual que el notebook.
        foreach (var promise in normalizedPromises.Where(IsWindowValid))
        {
            promise.HasPaymentInOwnWindow = realPayments.Any(payment =>
                SameClient(payment.ClientId, promise.ClientId)
                && InWindow(payment.Payment.ReceivedOn!.Value, promise));
        }

        foreach (var group in normalizedPromises
            .Where(promise => IsWindowValid(promise) && promise.Source.CreatedAt is not null)
            .GroupBy(promise => promise.ClientId, StringComparer.OrdinalIgnoreCase))
        {
            var originals = new List<PromiseState>();
            foreach (var promise in group.OrderBy(promise => promise.Source.CreatedAt)
                .ThenBy(promise => NumericSuffix(promise.Source.VisibleId))
                .ThenBy(promise => promise.Position))
            {
                var blockers = originals.Where(original =>
                    DateOnly.FromDateTime(promise.Source.CreatedAt!.Value) >= original.Start!.Value
                    && DateOnly.FromDateTime(promise.Source.CreatedAt!.Value) < original.EndExclusive!.Value
                    && !original.HasPaymentInOwnWindow).ToArray();
                promise.Robbed = blockers.Any(blocker => !SameAgent(promise, blocker));
                promise.Duplicated = blockers.Length > 0 && !promise.Robbed;
                if (!promise.Duplicated && !promise.Robbed) originals.Add(promise);
            }
        }

        var eligible = normalizedPromises.Where(promise => IsWindowValid(promise)
            && !promise.Duplicated && !promise.Robbed).ToArray();
        var assignments = new Dictionary<long, Assignment>();
        foreach (var payment in realPayments.OrderBy(payment => payment.Position))
        {
            var candidate = eligible.Where(promise => SameClient(payment.ClientId, promise.ClientId)
                    && InWindow(payment.Payment.ReceivedOn!.Value, promise))
                .OrderBy(promise => promise.Source.CreatedAt)
                .ThenBy(promise => promise.Position)
                .FirstOrDefault();
            if (candidate is not null)
            {
                assignments[payment.Payment.SourceId] = new(candidate, "Vigente");
                candidate.Active = true;
            }
        }

        foreach (var payment in realPayments.Where(payment => !assignments.ContainsKey(payment.Payment.SourceId))
            .OrderBy(payment => payment.Position))
        {
            var paymentDate = payment.Payment.ReceivedOn!.Value;
            var isoYear = ISOWeek.GetYear(paymentDate.ToDateTime(TimeOnly.MinValue));
            var isoWeek = ISOWeek.GetWeekOfYear(paymentDate.ToDateTime(TimeOnly.MinValue));
            var candidate = eligible.Where(promise => SameClient(payment.ClientId, promise.ClientId)
                    && promise.Source.CreatedAt is not null
                    && ISOWeek.GetYear(promise.Source.CreatedAt.Value) == isoYear
                    && ISOWeek.GetWeekOfYear(promise.Source.CreatedAt.Value) == isoWeek
                    && paymentDate >= promise.EndExclusive!.Value)
                .OrderBy(promise => promise.Source.CreatedAt)
                .ThenBy(promise => promise.Position)
                .FirstOrDefault();
            if (candidate is not null) assignments[payment.Payment.SourceId] = new(candidate, "Tardía");
        }

        var zones = geography.Select(row => (ClientId: Clean(row.ClientId), Zone: Clean(row.Zone)))
            .Where(row => row.ClientId.Length > 0 && row.Zone.Length > 0)
            .GroupBy(row => row.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Zone, StringComparer.OrdinalIgnoreCase);
        var effectivePortfolio = portfolio.Select(row =>
        {
            var clientId = Clean(row.ClientId);
            return Clean(row.Zone).Length > 0 || !zones.TryGetValue(clientId, out var zone)
                ? row : row with { Zone = zone };
        }).ToArray();
        var currentPortfolio = effectivePortfolio.Select((row, position) => new { Row = row, Position = position, ClientId = Clean(row.ClientId) })
            .Where(row => row.ClientId.Length > 0)
            .GroupBy(row => row.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Position).First().Row, StringComparer.OrdinalIgnoreCase);
        var contacts = BuildContacts(effectivePortfolio, references, contactSeeds, currentPortfolio);
        var outputPayments = payments.Select(payment =>
        {
            var clientId = Clean(payment.ClientId);
            assignments.TryGetValue(payment.SourceId, out var assignment);
            currentPortfolio.TryGetValue(clientId, out var client);
            var receivedOn = payment.ReceivedOn;
            return new ProcessedPayment(payment.SourceId, clientId, receivedOn, payment.Recovery, payment.Week,
                Clean(payment.Product), Clean(payment.Concept), Clean(payment.PaymentPlan),
                Clean(payment.Zone).Length > 0 ? Clean(payment.Zone) : client is null ? string.Empty : Clean(client.Zone),
                assignment?.Promise.AgentName ?? "Orgánico", assignment?.Promise.AgentId ?? string.Empty,
                client is null ? string.Empty : Clean(client.ClientName), DayName(receivedOn),
                assignment?.Promise.Source.VisibleId ?? string.Empty, assignment?.Promise.Source.DatabasePromiseId,
                assignment?.Type ?? "Orgánico");
        }).OrderBy(payment => payment.SourceId).ToArray();
        var outputPromises = normalizedPromises.Select(promise =>
        {
            currentPortfolio.TryGetValue(promise.ClientId, out var client);
            var status = promise.Robbed ? "Robada"
                : promise.Duplicated ? "Duplicada"
                : promise.Active ? "Activa"
                : IsWindowValid(promise) && cutoff >= promise.Start!.Value && cutoff < promise.EndExclusive!.Value ? "Vigente"
                : "Incumplida";
            return new ProcessedPromise(promise.Source.DatabasePromiseId, promise.Source.VisibleId,
                promise.Source.AscendraRegistrationId, promise.ClientId, client is null ? string.Empty : Clean(client.ClientName),
                Clean(promise.Source.ClientNumber), promise.AgentName, promise.AgentId, promise.Source.PromiseDate,
                promise.Source.InitialAmount, promise.Source.WeeklyAmount, Clean(promise.Source.PromiseType),
                promise.Source.NumberOfWeeks, promise.Source.CreatedAt, status);
        }).OrderByDescending(promise => promise.PromiseDate).ThenBy(promise => promise.DatabasePromiseId).ToArray();
        var outputPortfolio = effectivePortfolio.Select(row => new ProcessedPortfolio(Clean(row.ClientId), Clean(row.ClientName),
                Clean(row.Product), row.TotalBalance, Clean(row.Status), Clean(row.PaymentDay), Clean(row.Zone),
                row.Phones.Select(Phone).Where(phone => phone.Length > 0).Distinct(StringComparer.Ordinal).ToArray()))
            .Where(row => row.ClientId.Length > 0).ToArray();
        return new OperationalProcessingResult(outputPortfolio, contacts, outputPayments, outputPromises);
    }

    private static IReadOnlyList<ProcessedContact> BuildContacts(IReadOnlyList<OperationalPortfolio> portfolio,
        IReadOnlyList<OperationalReference> references,
        IReadOnlyList<OperationalContactSeed> contactSeeds,
        IReadOnlyDictionary<string, OperationalPortfolio> currentPortfolio)
    {
        var portfolioPairs = new HashSet<(string ClientId, string Phone)>();
        foreach (var row in portfolio)
        {
            var clientId = Clean(row.ClientId);
            if (clientId.Length == 0) continue;
            foreach (var rawPhone in row.Phones)
            {
                var phone = Phone(rawPhone);
                if (phone.Length > 0) portfolioPairs.Add((clientId, phone));
            }
        }
        var referenceRecords = new HashSet<(string ClientId, string Phone, string Name, string Relationship)>();
        var referencePairs = new HashSet<(string ClientId, string Phone)>();
        foreach (var row in references)
        {
            var clientId = Clean(row.ClientId); var phone = Phone(row.Phone);
            if (clientId.Length == 0 || phone.Length == 0) continue;
            referenceRecords.Add((clientId, phone, Clean(row.ReferenceName), Clean(row.Relationship)));
            referencePairs.Add((clientId, phone));
        }
        foreach (var seed in contactSeeds)
        {
            var clientId = Clean(seed.ClientId); var phone = Phone(seed.Phone);
            if (clientId.Length == 0 || phone.Length == 0) continue;
            if (Clean(seed.Source).Equals("Cartera", StringComparison.OrdinalIgnoreCase))
            {
                portfolioPairs.Add((clientId, phone));
                continue;
            }
            referenceRecords.Add((clientId, phone, Clean(seed.ReferenceName), Clean(seed.Relationship)));
            referencePairs.Add((clientId, phone));
        }
        var contacts = new List<ProcessedContact>();
        foreach (var reference in referenceRecords)
        {
            currentPortfolio.TryGetValue(reference.ClientId, out var client);
            contacts.Add(new(reference.ClientId, reference.Phone, reference.Name, reference.Relationship,
                client is null ? string.Empty : Clean(client.ClientName), client is null ? string.Empty : Clean(client.Product),
                client?.TotalBalance, client is null ? "Inactivo" : "Activo", "Referencia"));
        }
        foreach (var pair in portfolioPairs.Except(referencePairs))
        {
            currentPortfolio.TryGetValue(pair.ClientId, out var client);
            contacts.Add(new(pair.ClientId, pair.Phone, string.Empty, string.Empty,
                client is null ? string.Empty : Clean(client.ClientName), client is null ? string.Empty : Clean(client.Product),
                client?.TotalBalance, client is null ? "Inactivo" : "Activo", "Cartera"));
        }
        return contacts.OrderBy(row => row.ClientId, StringComparer.Ordinal)
            .ThenBy(row => row.Phone, StringComparer.Ordinal).ThenBy(row => row.Source, StringComparer.Ordinal)
            .ThenBy(row => row.ReferenceName, StringComparer.Ordinal).ThenBy(row => row.Relationship, StringComparer.Ordinal).ToArray();
    }

    private static bool IsWindowValid(PromiseState promise) => promise.ClientId.Length > 0
        && promise.Start is not null && promise.EndExclusive is not null && promise.Start < promise.EndExclusive;
    private static bool InWindow(DateOnly date, PromiseState promise) => date >= promise.Start!.Value && date < promise.EndExclusive!.Value;
    private static bool SameClient(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool SameAgent(PromiseState left, PromiseState right)
    {
        if (left.AgentId.Length > 0 && right.AgentId.Length > 0)
            return left.AgentId.Equals(right.AgentId, StringComparison.OrdinalIgnoreCase);
        if (left.AgentName.Length > 0 && right.AgentName.Length > 0)
            return left.AgentName.Equals(right.AgentName, StringComparison.OrdinalIgnoreCase);
        return true;
    }
    private static string Clean(string? value) => (value ?? string.Empty).Replace('\u00a0', ' ').Trim();
    private static string AgentId(string? value)
    {
        var cleaned = Clean(value);
        return Regex.Replace(cleaned, @"^(\d+)\.0+$", "$1");
    }
    private static string FormatAgent(string? value)
    {
        var text = Clean(value);
        if (text.Length == 0) return string.Empty;
        var culture = CultureInfo.GetCultureInfo("es-MX");
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(3)
            .Select(token => culture.TextInfo.ToTitleCase(token.ToLower(culture))));
    }
    private static string Phone(string? value)
    {
        var digits = Regex.Replace(Clean(value), "\\D", string.Empty);
        if (digits.Length == 13 && digits.StartsWith("521", StringComparison.Ordinal)) digits = digits[3..];
        else if (digits.Length == 12 && digits.StartsWith("52", StringComparison.Ordinal)) digits = digits[2..];
        return digits.Length < 7 || digits.All(digit => digit == '0') ? string.Empty : digits;
    }
    private static double NumericSuffix(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"(\d+)$");
        return match.Success && double.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : double.PositiveInfinity;
    }
    private static string DayName(DateOnly? date) => date?.DayOfWeek switch
    {
        DayOfWeek.Monday => "Lunes", DayOfWeek.Tuesday => "Martes", DayOfWeek.Wednesday => "Miércoles",
        DayOfWeek.Thursday => "Jueves", DayOfWeek.Friday => "Viernes", DayOfWeek.Saturday => "Sábado",
        DayOfWeek.Sunday => "Domingo", _ => string.Empty,
    };

    private sealed class PromiseState(OperationalPromise source, int position, string clientId, string agentName,
        string agentId, DateOnly? start, DateOnly? endExclusive)
    {
        public OperationalPromise Source { get; } = source;
        public int Position { get; } = position;
        public string ClientId { get; } = clientId;
        public string AgentName { get; } = agentName;
        public string AgentId { get; } = agentId;
        public DateOnly? Start { get; } = start;
        public DateOnly? EndExclusive { get; } = endExclusive;
        public bool HasPaymentInOwnWindow { get; set; }
        public bool Duplicated { get; set; }
        public bool Robbed { get; set; }
        public bool Active { get; set; }
    }
    private sealed record PaymentState(OperationalPayment Payment, int Position, string ClientId);
    private sealed record Assignment(PromiseState Promise, string Type);
}
