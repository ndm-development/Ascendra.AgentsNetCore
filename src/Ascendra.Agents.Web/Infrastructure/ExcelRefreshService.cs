namespace Ascendra.Agents.Web.Infrastructure;

public sealed class ExcelRefreshService(ExcelSnapshotStore store, ILogger<ExcelRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(store.RefreshSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await store.RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Actualización de Excel detenida.");
        }
    }
}
