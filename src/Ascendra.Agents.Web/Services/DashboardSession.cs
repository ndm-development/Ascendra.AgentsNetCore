using System.Security.Claims;
using Ascendra.Agents.Web.Auth;
using Ascendra.Agents.Web.Domain;
using Ascendra.Agents.Web.Infrastructure;

namespace Ascendra.Agents.Web.Services;

/// <summary>Estado mínimo por circuito; los datos operativos nunca se guardan en el navegador.</summary>
public sealed class DashboardSession(ExcelSnapshotStore snapshots)
{
    public AgentUser? User { get; private set; }
    public bool IsAgent => User?.IsAgent == true;
    public bool HasFullAccess => User?.HasFullAccess == true;

    public string Greeting
    {
        get
        {
            var name = ShortName;
            return string.IsNullOrWhiteSpace(name) ? "Buen ritmo. Sigue así." : $"Buen ritmo, {name}. Sigue asi!.";
        }
    }

    public string ShortName
    {
        get
        {
            if (User is null) return string.Empty;
            var firstName = User.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? User.DisplayName;
            var paternal = snapshots.Current.AgentDirectory.FirstOrDefault(x => x.Id == User.Id)?.PaternalSurname;
            return string.IsNullOrWhiteSpace(paternal) ? firstName : $"{firstName} {paternal}";
        }
    }

    public void Apply(ClaimsPrincipal principal)
    {
        var username = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var displayName = principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? "Agente";
        var agentId = principal.FindFirstValue(AgentClaimTypes.AgentId) ?? string.Empty;
        User = new AgentUser(agentId, username, displayName, role, true);
    }
}
