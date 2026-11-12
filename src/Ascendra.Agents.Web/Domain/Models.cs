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

public sealed record AgentDirectoryRecord(string Id, string FirstName, string PaternalSurname);

public sealed record AgentDataSnapshot(
    string Version,
    DateTimeOffset LoadedAt,
    IReadOnlyList<AgentUser> Users,
    IReadOnlyList<PaymentRecord> Payments,
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
