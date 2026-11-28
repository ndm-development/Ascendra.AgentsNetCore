using Ascendra.Agents.Web.Services;
using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Web.Infrastructure;

public sealed class ExcelRefreshService(
    ExcelSnapshotStore store,
    PromiseRegistrationService promiseRegistrations,
    IOptions<OperationalProcessingOptions> processingOptions,
    ILogger<ExcelRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAndSynchronizeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(store.RefreshSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshAndSynchronizeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Actualización de Excel detenida.");
        }
    }

    private async Task RefreshAndSynchronizeAsync(CancellationToken cancellationToken)
    {
        await store.RefreshAsync(cancellationToken);
        // En modo PostgreSQL el mismo proceso publica y audita los estatus en
        // la transacción del corte; la sincronización heredada sólo existe
        // mientras la fuente sean los dos Excel generados por Python.
        if (!processingOptions.Value.UsePostgres)
        {
            await promiseRegistrations.SynchronizeReconciledStatusesAsync(cancellationToken);
        }
    }
}
