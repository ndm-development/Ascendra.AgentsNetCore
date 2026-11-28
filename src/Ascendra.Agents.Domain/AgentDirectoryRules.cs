namespace Ascendra.Agents.Domain;

public static class AgentDirectoryRules
{
    public static readonly IReadOnlyList<string> Roles = ["Agente", "Supervisor", "Gerente", "Director"];

    public static IReadOnlyList<AgentDirectoryRecord> Validate(
        IEnumerable<AgentDirectoryRecord> source, bool requireActiveUsers = false)
    {
        var entries = source.Select(agent => agent with
        {
            Id = IdentityNormalization.AgentId(agent.Id),
            FirstName = Clean(agent.FirstName),
            PaternalSurname = Clean(agent.PaternalSurname),
            MaternalSurname = Clean(agent.MaternalSurname),
            DisplayAlias = Clean(agent.DisplayAlias),
            Username = IdentityNormalization.Normalize(agent.Username),
            Role = Roles.FirstOrDefault(role => role.Equals(agent.Role?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("El rol de una persona no es válido."),
        }).OrderBy(agent => agent.Id, StringComparer.Ordinal).ToArray();

        if (entries.Length == 0) throw new InvalidOperationException("El padrón no contiene personas.");
        if (entries.Any(agent => string.IsNullOrWhiteSpace(agent.Id)
            || string.IsNullOrWhiteSpace(agent.FirstName) || string.IsNullOrWhiteSpace(agent.PaternalSurname)))
            throw new InvalidOperationException("Cada persona requiere ID, nombre y apellido paterno.");
        if (entries.Any(agent => agent.Id.Length > 20 || agent.DisplayName.Length > 100))
            throw new InvalidOperationException("El ID admite hasta 20 caracteres y el nombre completo hasta 100.");
        if (entries.GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("El padrón contiene IDs duplicados.");
        var active = entries.Where(agent => agent.IsActive).ToArray();
        if (requireActiveUsers && active.Length == 0)
            throw new InvalidOperationException("La carga inicial requiere al menos un usuario activo.");
        if (active.Any(agent => agent.Username is "" or "n/a" or "na"))
            throw new InvalidOperationException("Cada persona activa requiere un usuario válido.");
        if (active.GroupBy(agent => agent.Username, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("El padrón contiene usuarios activos duplicados.");
        return Array.AsReadOnly(entries);
    }

    public static bool CanManage(AgentDirectoryRecord? actor) =>
        actor is { IsActive: true } && OperationalPermissions.CanManageDirectory(actor.Role);

    private static string Clean(string? value) => string.Join(' ', (value ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
