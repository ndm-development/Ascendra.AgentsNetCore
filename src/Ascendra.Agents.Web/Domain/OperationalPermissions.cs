namespace Ascendra.Agents.Web.Domain;

/// <summary>
/// Reglas de acceso centralizadas para evitar que una misma autorización se
/// replique de forma distinta entre páginas, servicios y acciones.
/// </summary>
public static class OperationalPermissions
{
    public const string TeamConsoleRoles = "Gerente,Supervisor,Director";
    public const string PromiseManagementRoles = "Gerente,Director";

    public static bool CanManagePromises(string? role) =>
        role?.Equals("Gerente", StringComparison.OrdinalIgnoreCase) == true
        || role?.Equals("Director", StringComparison.OrdinalIgnoreCase) == true;
}
