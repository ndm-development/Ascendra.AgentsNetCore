namespace Ascendra.Agents.Web.Domain;

public sealed record AgentUser(
    string Id,
    string Username,
    string DisplayName,
    string Role,
    bool IsActive)
{
    public bool HasFullAccess => Role.Equals("Gerente", StringComparison.OrdinalIgnoreCase)
        || Role.Equals("Supervisor", StringComparison.OrdinalIgnoreCase);
    public bool IsManager => Role.Equals("Gerente", StringComparison.OrdinalIgnoreCase);
    public bool IsSupervisor => Role.Equals("Supervisor", StringComparison.OrdinalIgnoreCase);
    public bool IsAgent => Role.Equals("Agente", StringComparison.OrdinalIgnoreCase);
}

public sealed record PaymentRecord(
    string Agent,
    string AgentId,
    DateOnly? ReceivedOn,
    string Product,
    string Concept,
    decimal Recovery,
    int? Week,
    string ClientId,
    string ClientName,
    string Day,
    string PaymentPlan,
    string PromiseId,
    string AssignmentType,
    string Zone);

public sealed record PromiseRecord(
    string Id,
    string ClientId,
    string ClientName,
    string ClientNumber,
    string Agent,
    string AgentId,
    DateOnly? PromiseDate,
    decimal InitialAmount,
    decimal WeeklyAmount,
    string Type,
    int? NumberOfWeeks,
    DateOnly? CreatedOn,
    string Status);

public sealed record PortfolioRecord(
    string ClientId,
    string ClientName,
    string Product,
    decimal? TotalBalance,
    string Status,
    string PaymentDay,
    string Zone,
    IReadOnlyList<string> Phones);

public sealed record ContactRecord(
    string ClientId,
    string Phone,
    string ReferenceName,
    string Relationship,
    string ClientName,
    string Product,
    decimal? TotalBalance,
    string Status,
    string Source);

/// <summary>
/// Resultado acotado de la consulta de Referencias. Conserva los datos de
/// contacto y únicamente expone quién registró la última promesa del cliente
/// junto con su estatus; no incluye el detalle de promesas de otros agentes.
/// </summary>
public sealed record ReferenceLookupResult(
    ContactRecord Contact,
    string LatestPromiseAgent,
    string LatestPromiseStatus);

/// <summary>
/// Catálogo visible de resultados de llamada. El código y la ponderación se
/// resuelven exclusivamente en el servidor para que no formen parte de la UI.
/// </summary>
public sealed record CallDispositionCategory(
    string Name,
    IReadOnlyList<string> Results);

/// <summary>
/// Vista operativa de una tipificación ya registrada. El evento queda ligado
/// al cliente y conserva el teléfono trabajado como evidencia de la llamada.
/// </summary>
public sealed record CallDispositionEntry(
    Guid Id,
    DateTimeOffset RecordedAt,
    string ClientId,
    string AgentId,
    string AgentName,
    string Category,
    string Result,
    string Channel,
    string Phone,
    string Comment);

public sealed record ClientDispositionHistory(
    IReadOnlyList<CallDispositionEntry> Entries,
    int TotalEntries,
    CallDispositionEntry? HighestWeight)
{
    public static ClientDispositionHistory Empty { get; } = new([], 0, null);
}

/// <summary>
/// Resumen de la disposición con mayor ponderación para cada cliente con
/// actividad. Sólo se usa en la vista consolidada de Gerencia.
/// </summary>
public sealed record ClientDispositionSummary(
    string ClientId,
    DateTimeOffset RecordedAt,
    string AgentId,
    string AgentName,
    string Category,
    string Result,
    string Channel,
    string Phone);

public sealed record AgentDirectoryRecord(string Id, string FirstName, string PaternalSurname);

public sealed record AgentDataSnapshot(
    string Version,
    DateTimeOffset LoadedAt,
    IReadOnlyList<AgentUser> Users,
    IReadOnlyList<PaymentRecord> Payments,
    IReadOnlyList<PaymentRecord> OrganicPayments,
    IReadOnlyList<PromiseRecord> Promises,
    IReadOnlyList<PortfolioRecord> Portfolio,
    IReadOnlyList<ContactRecord> Contacts,
    IReadOnlyList<AgentDirectoryRecord> AgentDirectory,
    IReadOnlyList<string> Warnings)
{
    public static AgentDataSnapshot Empty { get; } = new(
        "pending",
        DateTimeOffset.MinValue,
        Array.Empty<AgentUser>(),
        Array.Empty<PaymentRecord>(),
        Array.Empty<PaymentRecord>(),
        Array.Empty<PromiseRecord>(),
        Array.Empty<PortfolioRecord>(),
        Array.Empty<ContactRecord>(),
        Array.Empty<AgentDirectoryRecord>(),
        Array.Empty<string>());
}

public sealed record DataHealth(bool IsReady, string Message, DateTimeOffset? LoadedAt, string Version);

public sealed record ScopedOperations(
    IReadOnlyList<PaymentRecord> Payments,
    IReadOnlyList<PromiseRecord> Promises,
    IReadOnlyList<PortfolioRecord> Portfolio,
    IReadOnlyList<int> Weeks);

public sealed record MetricCard(string Label, string Value, string Supporting, string Tone = "neutral");

public sealed record ChartModel(object[] Data, object Layout, object? Config = null);

/// <summary>
/// Marca elegida en una gráfica Plotly. Sólo conserva el cruce operativo que
/// permite consultar el detalle en el servidor.
/// </summary>
public sealed record ChartPointSelection(string X, string TraceName);

public sealed record WeeklyPulse(
    decimal Recovery,
    int PaymentCount,
    int ClientCount,
    string TierLabel,
    decimal EstimatedBonus,
    decimal? RemainingToNextTier,
    string NextTierLabel,
    decimal? NextTierBonus,
    double Progress);

/// <summary>
/// Posición semanal expuesta a Agentes: sólo nombre corto e importe agregado.
/// No contiene clientes, pagos individuales ni promesas de terceros.
/// </summary>
public sealed record WeeklyAgentRecoveryRank(
    int Position,
    string Agent,
    decimal Recovery,
    bool IsCurrentUser);

public sealed record PromiseComplianceSummary(
    decimal? Rate,
    int Active,
    int Open,
    int Failed,
    int Stolen,
    int Duplicate,
    int Eligible,
    string Focus);

public sealed record WhatsAppTemplate(string Value, string Label, string Message);
