using Ascendra.Agents.Domain;

namespace Ascendra.Agents.Application;

/// <summary>Fuentes necesarias para sustituir el corte Excel completo.</summary>
public sealed record OperationalProcessingInput(
    string SourceFingerprint,
    IReadOnlyList<OperationalPayment> Payments,
    IReadOnlyList<OperationalPromise> Promises,
    IReadOnlyList<OperationalPortfolio> Portfolio,
    IReadOnlyList<OperationalReference> References,
    IReadOnlyList<OperationalContactSeed> ContactSeeds,
    IReadOnlyList<OperationalGeography> Geography,
    IReadOnlyList<OperationalAgent> Agents);

public sealed record OperationalProcessingRun(
    Guid Id, string SourceFingerprint, DateTimeOffset CompletedAt,
    OperationalProcessingResult Result);

public interface IOperationalProcessingRepository
{
    Task<OperationalProcessingInput> ReadInputAsync(CancellationToken cancellationToken = default);
    Task<OperationalProcessingRun?> ReadCurrentAsync(CancellationToken cancellationToken = default);
    Task<OperationalProcessingRun> SaveAsync(string sourceFingerprint, OperationalProcessingResult result,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Caso de uso único: toma las versiones vigentes en PostgreSQL, ejecuta las
/// reglas y publica contactos, pagos atribuidos y estatus en una transacción.
/// </summary>
public sealed class OperationalProcessingService(IOperationalProcessingRepository repository)
{
    public Task<OperationalProcessingRun?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        repository.ReadCurrentAsync(cancellationToken);

    public async Task<OperationalProcessingRun> ProcessAsync(DateOnly cutoff, CancellationToken cancellationToken = default)
    {
        var input = await repository.ReadInputAsync(cancellationToken);
        // El corte cambia Vigente/Incumplida aun cuando no llegue un archivo.
        // Incorporar la fecha operativa evita que una promesa venza sólo al
        // siguiente pago o carga de cartera.
        var fingerprint = $"{input.SourceFingerprint}:{cutoff:yyyyMMdd}";
        var current = await repository.ReadCurrentAsync(cancellationToken);
        if (current is not null && current.SourceFingerprint == fingerprint) return current;

        var result = OperationalProcessingRules.Process(input.Payments, input.Promises, input.Portfolio,
            input.References, input.ContactSeeds, input.Geography, input.Agents, cutoff);
        return await repository.SaveAsync(fingerprint, result, cancellationToken);
    }
}
