using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Infrastructure;

/// <summary>Interruptor explícito: no cambia la fuente del CRM al desplegar código.</summary>
public sealed class OperationalProcessingOptions
{
    public const string SectionName = "OperationalProcessing";
    public bool UsePostgres { get; init; }
    public int RefreshSeconds { get; init; } = 15;
}

/// <summary>
/// Recalcula sólo cuando cambió una fuente vigente o una promesa. El servicio
/// es seguro frente a varias instancias: el repositorio publica un único corte
/// bajo bloqueo transaccional.
/// </summary>
public sealed class OperationalProcessingWorker(
    IOptionsMonitor<OperationalProcessingOptions> options,
    OperationalProcessingService processing,
    ILogger<OperationalProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.UsePostgres)
                {
                    var run = await processing.ProcessAsync(MexicoDate(DateTimeOffset.UtcNow), stoppingToken);
                    logger.LogInformation("Proceso operativo disponible: {Run}; contactos {Contacts}; pagos {Payments}; promesas {Promises}.",
                        run.Id, run.Result.Contacts.Count, run.Result.Payments.Count, run.Result.Promises.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // No borra el corte previo ni detiene el CRM si una carga llegó incompleta.
                logger.LogError(exception, "No se pudo ejecutar el proceso operativo de PostgreSQL.");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.RefreshSeconds, 15, 900)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private static DateOnly MexicoDate(DateTimeOffset instant) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeBySystemTimeZoneId(instant, "America/Mexico_City").DateTime);
}
