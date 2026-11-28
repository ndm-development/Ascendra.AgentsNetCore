namespace Ascendra.Agents.Web.Infrastructure;

public sealed class AgentDirectoryOptions
{
    // La activación se hace después de validar el padrón y los accesos del entorno.
    public bool UsePostgres { get; init; } = false;
}
