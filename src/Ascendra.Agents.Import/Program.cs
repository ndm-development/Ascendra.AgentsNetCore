using System.Text.Json;
using Ascendra.Agents.Application;
using Ascendra.Agents.Domain;
using Ascendra.Agents.Infrastructure;
using Npgsql;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
var apply = args.Contains("--apply", StringComparer.Ordinal);
string? Argument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var manifestPath = Argument("--manifest");
var reportPath = Argument("--report");
if (manifestPath is null || reportPath is null || apply && Argument("--expected") is null)
{
    Console.Error.WriteLine("Uso: --manifest fuentes.json --report resultado.json [--apply --expected plan.json]. Conexión: ASCENDRA_IMPORT_CONNECTION.");
    return 2;
}
var batches = new List<DataImportBatch>();
ImportReport? report = null;
void SaveReport()
{
    var output = Path.GetFullPath(reportPath);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output + ".tmp", JsonSerializer.Serialize(report, jsonOptions));
    File.Move(output + ".tmp", output, overwrite: true);
}
try
{
    var sources = JsonSerializer.Deserialize<List<SourceFile>>(File.ReadAllText(manifestPath), jsonOptions)
        ?? throw new InvalidDataException("Manifest vacío.");
    var reader = new OperationalWorkbookReader();
    foreach (var source in sources)
    {
        var loaded = reader.Read(source.Dataset, source.Path, source.SheetName, source.SourceKey);
        batches.AddRange(loaded);
        Console.WriteLine($"Preparado {source.Dataset}/{Path.GetFileName(source.Path)}: {loaded.Sum(batch => batch.Rows.Count)} filas.");
    }
    if (batches.GroupBy(batch => (batch.Dataset, batch.SourceKey)).Any(group => group.Count() > 1))
        throw new InvalidDataException("El manifiesto contiene fuentes duplicadas para el mismo conjunto y corte.");
    var summaries = batches.Select(batch =>
    {
        var amountIndex = batch.Columns.ToList().FindIndex(column => column.Name == "recuperacion_por_gestion");
        return new BatchSummary(batch.Dataset, batch.SourceKey, batch.FileName, batch.SheetName, batch.Fingerprint,
            batch.Rows.Count, batch.Rows.Count(row => row.Issues is not null),
            amountIndex < 0 ? null : batch.Rows.Sum(row => row.Values[amountIndex] as decimal? ?? 0m));
    }).ToArray();
    report = new(apply ? "apply" : "plan", DateTimeOffset.UtcNow, summaries, []);
    if (apply)
    {
        var expected = JsonSerializer.Deserialize<ImportReport>(File.ReadAllText(Argument("--expected")!), jsonOptions)
            ?? throw new InvalidDataException("No existe el plan esperado.");
        if (!expected.Completed || !expected.Sources.SequenceEqual(summaries))
            throw new InvalidDataException("Los archivos cambiaron desde el plan; prepara una nueva revisión antes de cargar.");
        var connection = Environment.GetEnvironmentVariable("ASCENDRA_IMPORT_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Falta la conexión de importación.");
        var settings = PostgresDatabase.BuildConnectionString(connection, 5);
        await using var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var service = new OperationalImportService(new PostgresOperationalImportRepository(dataSource));
        SaveReport();
        foreach (var batch in batches)
        {
            var result = await service.ImportAsync(batch);
            report.Results.Add(result);
            SaveReport();
            Console.WriteLine($"{(result.Imported ? "Cargado" : "Verificado sin duplicar")} {result.Dataset}/{result.SourceKey}: {result.Rows} filas.");
        }
    }
    report.Completed = true;
    SaveReport();
    Console.WriteLine($"Completado: {summaries.Length} lotes, {summaries.Sum(source => source.Rows)} filas. Modo: {report.Mode}.");
    return 0;
}
catch (Exception exception)
{
    if (report is not null) { report.ErrorType = exception.GetType().Name; SaveReport(); }
    // Nunca imprimir cadenas de conexión, datos de clientes ni valores rechazados por PostgreSQL.
    var code = exception is PostgresException databaseError ? $", SQLSTATE {databaseError.SqlState}" : string.Empty;
    Console.Error.WriteLine($"Carga detenida ({exception.GetType().Name}{code}). Consulta el informe de lotes completados y verifica las fuentes/configuración.");
    return 1;
}

internal sealed record SourceFile(string Dataset, string Path, string? SheetName = null, string? SourceKey = null);
internal sealed record BatchSummary(string Dataset, string SourceKey, string FileName, string SheetName,
    string Fingerprint, int Rows, int IssueRows, decimal? Recovery);
internal sealed record ImportReport(string Mode, DateTimeOffset CreatedAt, IReadOnlyList<BatchSummary> Sources, List<DataImportResult> Results)
{
    public bool Completed { get; set; }
    public string? ErrorType { get; set; }
}
