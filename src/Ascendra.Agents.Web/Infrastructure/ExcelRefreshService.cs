using Ascendra.Agents.Web.Services;

namespace Ascendra.Agents.Web.Infrastructure;

public sealed class ExcelRefreshService(
    ExcelSnapshotStore store,
    PromiseRegistrationService promiseRegistrations,
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
        await promiseRegistrations.SynchronizeReconciledStatusesAsync(cancellationToken);
    }
}
