namespace Ascendra.Agents.Domain;

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
    public bool IsDirector => Role.Equals("Director", StringComparison.OrdinalIgnoreCase);
    public bool CanManagePromises => OperationalPermissions.CanManagePromises(Role);
    public bool IsAgent => Role.Equals("Agente", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Identidad canónica de una persona en Ascendra. El ID es la llave estable;
/// <see cref="DisplayName"/> es el nombre completo acordado para nuevas
/// operaciones y los demás nombres sólo ayudan a reconocer cortes históricos.
/// </summary>
public sealed record AgentDirectoryRecord(
    string Id,
    string FirstName,
    string PaternalSurname,
    string MaternalSurname,
    string DisplayAlias,
    string Username,
    string Role,
    bool IsActive,
    long Revision = 0)
{
    public string DisplayName => JoinName(FirstName, PaternalSurname, MaternalSurname);
    public AgentUser ToUser() => new(Id, Username, DisplayName, Role, IsActive);

    public IReadOnlyList<string> Aliases =>
        new[]
        {
            DisplayName,
            DisplayAlias,
            JoinName(FirstName, PaternalSurname, MaternalSurname),
            JoinName(FirstName, PaternalSurname),
            JoinName(FirstToken(FirstName), PaternalSurname),
            JoinName(FirstToken(FirstName), MaternalSurname),
        }
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string JoinName(params string[] values) => string.Join(' ', values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim()));

    private static string FirstToken(string value) => value
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault() ?? string.Empty;
}

