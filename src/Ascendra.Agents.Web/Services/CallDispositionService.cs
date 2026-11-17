using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ascendra.Agents.Web.Domain;
using Ascendra.Agents.Web.Infrastructure;
using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Web.Services;

/// <summary>
/// Conserva las tipificaciones como eventos inmutables por CLIENTE_UNICO.
/// Los Excel siguen siendo la fuente de cartera y de teléfonos; este journal
/// sólo registra la gestión hecha sobre un teléfono que exista en el corte.
/// </summary>
public sealed class CallDispositionService
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyList<CallDispositionDefinition> Definitions =
    [
        new("Contacto efectivo", "CT01", "Promesa cumplida", 230),
        new("Contacto efectivo", "CT02", "Seguimiento de pago", 220),
        new("Contacto efectivo", "CT03", "Promesa de pago", 210),
        new("Contacto efectivo", "CT04", "En negociación", 200),
        new("Contacto efectivo", "CT05", "Promesa incumplida", 190),
        new("Contacto efectivo", "CT06", "Negativa de pago", 180),
        new("Motivos Riesgo", "MR01", "Dificultad económica", 170),
        new("Motivos Riesgo", "MR02", "Desempleo", 160),
        new("Motivos Riesgo", "MR03", "Enfermedad", 150),
        new("Motivos Riesgo", "MR04", "Defunción", 140),
        new("Actualización Datos", "AD01", "Actualización teléfono", 130),
        new("Actualización Datos", "AD02", "Actualización domicilio", 120),
        new("Actualización Datos", "AD03", "Actualización correo", 110),
        new("Contacto con Tercero", "TR01", "Mensaje con familiar", 100),
        new("Contacto con Tercero", "TR02", "Mensaje con tercero", 90),
        new("Contacto con Tercero", "TR03", "Desconoce titular", 80),
        new("No Contacto", "NC01", "Línea ocupada", 70),
        new("No Contacto", "NC02", "Llamada rechazada", 60),
        new("No Contacto", "NC03", "No contesta", 50),
        new("No Contacto", "NC04", "Buzón de voz", 40),
        new("No Contacto", "NC05", "Apagado", 30),
        new("No Contacto", "NC06", "Fuera de cobertura", 20),
        new("No Contacto", "NC07", "Número inexistente", 10),
    ];

    private static readonly IReadOnlyList<CallDispositionCategory> VisibleCatalog = Definitions
        .GroupBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
        .Select(group => new CallDispositionCategory(
            group.Key,
            group.Select(definition => definition.Result).ToArray()))
        .ToArray();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _clientGates = new(StringComparer.Ordinal);
    private readonly ExcelSnapshotStore _snapshots;
    private readonly ILogger<CallDispositionService> _logger;
    private readonly string _storageDirectory;
    private readonly int _historyDisplayLimit;

    public CallDispositionService(
        ExcelSnapshotStore snapshots,
        IOptions<CallDispositionOptions> options,
        IWebHostEnvironment environment,
        ILogger<CallDispositionService> logger)
    {
        _snapshots = snapshots;
        _logger = logger;
        _storageDirectory = ResolveStorageDirectory(options.Value.StoragePath, environment);
        _historyDisplayLimit = Math.Clamp(options.Value.HistoryDisplayLimit, 20, 250);
    }

    /// <summary>Catálogo visible; no expone códigos ni pesos al navegador.</summary>
    public IReadOnlyList<CallDispositionCategory> Categories => VisibleCatalog;

    public IReadOnlyList<string> ResultsFor(string? category) => Definitions
        .Where(definition => definition.Category.Equals(category?.Trim(), StringComparison.OrdinalIgnoreCase))
        .Select(definition => definition.Result)
        .ToArray();

    public async Task<ClientDispositionHistory> GetHistoryAsync(
        string? clientId,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientId = NormalizeClientId(clientId);
        if (normalizedClientId.Length == 0) return ClientDispositionHistory.Empty;

        var gate = _clientGates.GetOrAdd(normalizedClientId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadHistoryLockedAsync(normalizedClientId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ClientDispositionHistory> RecordAsync(
        string? clientId,
        string? phone,
        string? category,
        string? result,
        string? agentId,
        string? agentName,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientId = NormalizeClientId(clientId);
        var normalizedPhone = TextNormalization.Phone(phone);
        if (normalizedClientId.Length == 0 || normalizedPhone.Length == 0)
        {
            throw new InvalidOperationException("Selecciona un cliente y un teléfono válido antes de registrar la llamada.");
        }

        var outcome = Definitions.FirstOrDefault(definition =>
            definition.Category.Equals(category?.Trim(), StringComparison.OrdinalIgnoreCase)
            && definition.Result.Equals(result?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (outcome is null)
        {
            throw new InvalidOperationException("Selecciona una categoría y un resultado válidos.");
        }

        var snapshotHasPhone = _snapshots.Current.Contacts.Any(contact =>
            contact.ClientId.Equals(normalizedClientId, StringComparison.OrdinalIgnoreCase)
            && contact.Phone.Equals(normalizedPhone, StringComparison.Ordinal));
        if (!snapshotHasPhone)
        {
            throw new InvalidOperationException("El teléfono ya no está asociado a ese CLIENTE_UNICO en el corte actual.");
        }

        var stored = new StoredCallDisposition(
            SchemaVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            normalizedClientId,
            normalizedPhone,
            NormalizeActorValue(agentId, "Sin ID"),
            NormalizeActorValue(agentName, "Agente Ascendra"),
            outcome.Category,
            outcome.Result,
            outcome.Code,
            outcome.Weight);

        var gate = _clientGates.GetOrAdd(normalizedClientId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_storageDirectory);
            var path = ClientPath(normalizedClientId);
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4 * 1024,
                useAsync: true);
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 4 * 1024,
                leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(stored, SerializerOptions));
            await writer.FlushAsync();
            stream.Flush(flushToDisk: true);
        }
        catch (IOException exception)
        {
            _logger.LogError(exception, "No se pudo guardar una tipificación de llamada.");
            throw new InvalidOperationException("No se pudo guardar la tipificación. Intenta nuevamente.");
        }
        finally
        {
            gate.Release();
        }

        return await GetHistoryAsync(normalizedClientId, cancellationToken);
    }

    private async Task<ClientDispositionHistory> ReadHistoryLockedAsync(
        string normalizedClientId,
        CancellationToken cancellationToken)
    {
        var path = ClientPath(normalizedClientId);
        if (!File.Exists(path)) return ClientDispositionHistory.Empty;

        var recent = new Queue<CallDispositionEntry>(_historyDisplayLimit);
        StoredCallDisposition? highest = null;
        var totalEntries = 0;
        var skippedMalformedLine = false;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            StoredCallDisposition? stored;
            try
            {
                stored = JsonSerializer.Deserialize<StoredCallDisposition>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                // Un corte abrupto puede dejar incompleta sólo la última línea.
                skippedMalformedLine = true;
                continue;
            }

            if (stored is null || !stored.ClientId.Equals(normalizedClientId, StringComparison.OrdinalIgnoreCase)) continue;

            totalEntries++;
            if (highest is null
                || stored.Weight > highest.Weight
                || stored.Weight == highest.Weight && stored.RecordedAt > highest.RecordedAt)
            {
                highest = stored;
            }

            recent.Enqueue(ToEntry(stored));
            while (recent.Count > _historyDisplayLimit) recent.Dequeue();
        }

        if (skippedMalformedLine)
        {
            _logger.LogWarning("Se ignoró una línea incompleta del historial de tipificaciones al consultar un cliente.");
        }

        return new ClientDispositionHistory(
            recent.Reverse().ToArray(),
            totalEntries,
            highest is null ? null : ToEntry(highest));
    }

    private string ClientPath(string normalizedClientId) => Path.Combine(
        _storageDirectory,
        $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedClientId))).ToLowerInvariant()}.jsonl");

    private static CallDispositionEntry ToEntry(StoredCallDisposition stored) => new(
        stored.Id,
        stored.RecordedAt,
        stored.ClientId,
        stored.AgentId,
        stored.AgentName,
        stored.Category,
        stored.Result,
        stored.Phone);

    private static string NormalizeClientId(string? value) => (value ?? string.Empty).Trim();
    private static string NormalizeActorValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string ResolveStorageDirectory(string? configuredPath, IWebHostEnvironment environment)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "/var/data/ascendra-call-dispositions"
            : configuredPath.Trim();
        if (!Path.IsPathRooted(path)) return Path.GetFullPath(path, environment.ContentRootPath);
        if (environment.IsDevelopment()
            && path.StartsWith("/var/data/", StringComparison.Ordinal)
            && !Directory.Exists("/var/data"))
        {
            return Path.Combine(environment.ContentRootPath, "data", "generated", Path.GetFileName(path));
        }
        return path;
    }

    private sealed record CallDispositionDefinition(
        string Category,
        string Code,
        string Result,
        int Weight);

    private sealed record StoredCallDisposition(
        int SchemaVersion,
        Guid Id,
        DateTimeOffset RecordedAt,
        string ClientId,
        string Phone,
        string AgentId,
        string AgentName,
        string Category,
        string Result,
        string Code,
        int Weight);
}

public sealed class CallDispositionOptions
{
    public const string SectionName = "CallDispositions";
    public string StoragePath { get; init; } = "/var/data/ascendra-call-dispositions";
    public int HistoryDisplayLimit { get; init; } = 80;
}
