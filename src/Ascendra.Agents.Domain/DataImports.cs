namespace Ascendra.Agents.Domain;

public enum ImportValueType { Text, Integer, Decimal, Timestamp }
public sealed record ImportColumn(string Name, string OriginalHeader, ImportValueType Type);
public sealed record ImportRow(int SourceRow, IReadOnlyList<object?> Values, string? Issues = null);
public sealed record DataImportBatch(string Dataset, string SourceKey, string FileName, string SheetName,
    string Fingerprint, IReadOnlyList<ImportColumn> Columns, IReadOnlyList<ImportRow> Rows);
public sealed record DataImportResult(string Dataset, string SourceKey, int Rows, bool Imported, bool Current);

public static class OperationalDatasets
{
    // Importaciones explícitas; nunca se permite que un archivo elija una tabla operativa existente.
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "pagos", "cartera", "referencias", "referencias_acumuladas", "contactos", "pagos_asignados",
        "promesas_conciliadas", "clientes_asignados", "demograficos", "catalogo_agentes",
        "catalogo_tipificaciones", "catalogo_cp_zona", "cuarteles", "competencia", "headcount_fuente",
        "promesas_historicas_fuente", "geografia_maestro", "saldos_historial"
    };

    public static string Table(string dataset) => Allowed.Contains(dataset)
        ? $"crm_{dataset}" : throw new InvalidOperationException("Conjunto de importación no permitido.");
}
