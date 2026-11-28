using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Web.Infrastructure;

public sealed class AgentDirectoryBootstrapService(
    IAgentDirectoryRepository repository, AgentDirectoryService directory,
    HeadcountImportReader reader, OperationalDataPaths paths,
    IOptions<AgentDirectoryOptions> options,
    ILogger<AgentDirectoryBootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.UsePostgres) return;
        // No abrir Excel, ni siquiera para calcular su huella, cuando la base ya tiene padrón.
        if ((await repository.ReadAsync(cancellationToken)).Count > 0)
        {
            await directory.GetAsync(cancellationToken);
            return;
        }
        var source = reader.Read(paths.Input("headcount.xlsx"));
        var result = await directory.SeedIfEmptyAsync(source.Directory, source.Fingerprint, cancellationToken);
        logger.LogInformation("Padrón PostgreSQL listo: {People} personas, {Active} activas; importación inicial: {Imported}.",
            result.People, result.ActiveUsers, result.Imported);
        foreach (var warning in source.Warnings) logger.LogWarning("{ImportWarning}", warning);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
