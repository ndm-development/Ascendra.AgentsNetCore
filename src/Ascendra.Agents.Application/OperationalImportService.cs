using Ascendra.Agents.Domain;

namespace Ascendra.Agents.Application;

public interface IOperationalImportRepository
{
    Task<DataImportResult> ImportAsync(DataImportBatch batch, CancellationToken cancellationToken = default);
}

public sealed class OperationalImportService(IOperationalImportRepository repository)
{
    public Task<DataImportResult> ImportAsync(DataImportBatch batch, CancellationToken cancellationToken = default)
    {
        OperationalDatasets.Table(batch.Dataset);
        if (string.IsNullOrWhiteSpace(batch.SourceKey) || string.IsNullOrWhiteSpace(batch.Fingerprint))
            throw new InvalidOperationException("La importación requiere procedencia y huella del archivo.");
        if (batch.Rows.Any(row => row.Values.Count != batch.Columns.Count)
            || batch.Rows.Select(row => row.SourceRow).Distinct().Count() != batch.Rows.Count)
            throw new InvalidOperationException("Las filas no corresponden al esquema del archivo.");
        return repository.ImportAsync(batch, cancellationToken);
    }
}
