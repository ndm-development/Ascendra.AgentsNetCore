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
    private const int SchemaVersion = 2;
    private const string SummaryIndexFileName = "summary-index-v2.jsonl";
    private static readonly IReadOnlyList<string> AvailableChannels =
        ["Predictivo", "WhatsApp", "Manual", "SMS"];
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
    private readonly SemaphoreSlim _summaryIndexGate = new(1, 1);
    private readonly ExcelSnapshotStore _snapshots;
    private readonly ILogger<CallDispositionService> _logger;
    private readonly string _storageDirectory;
    private readonly int _historyDisplayLimit;
    private IReadOnlyList<ClientDispositionSummary>? _summaryCache;

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
    public IReadOnlyList<string> Channels => AvailableChannels;

    public IReadOnlyList<string> ResultsFor(string? category) => Definitions
        .Where(definition => definition.Category.Equals(category?.Trim(), StringComparison.OrdinalIgnoreCase))
        .Select(definition => definition.Result)
        .ToArray();

    /// <summary>
    /// Devuelve una fila por CLIENTE_UNICO que tenga tipificaciones, con la
    /// disposición de mayor peso. El índice se deriva de los journals, por lo
    /// que puede reconstruirse sin depender de una base de datos adicional.
    /// </summary>
    public async Task<IReadOnlyList<ClientDispositionSummary>> GetHighestWeightSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSummaryIndexAsync(cancellationToken);

        await _summaryIndexGate.WaitAsync(cancellationToken);
        try
        {
            if (_summaryCache is not null) return _summaryCache;

            var highestByClient = new Dictionary<string, StoredDispositionSummary>(StringComparer.OrdinalIgnoreCase);
            var skippedMalformedLine = false;
            await using var stream = new FileStream(
                SummaryIndexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 8 * 1024,
                useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                StoredDispositionSummary? stored;
                try
                {
                    stored = JsonSerializer.Deserialize<StoredDispositionSummary>(line, SerializerOptions);
                }
                catch (JsonException)
                {
                    skippedMalformedLine = true;
                    continue;
                }

                if (stored is null || string.IsNullOrWhiteSpace(stored.ClientId)) continue;
                if (!highestByClient.TryGetValue(stored.ClientId, out var current) || IsHigher(stored, current))
                {
                    highestByClient[stored.ClientId] = stored;
                }
            }

            if (skippedMalformedLine)
            {
                _logger.LogWarning("Se ignoró una línea incompleta del índice de tipificaciones.");
            }

            _summaryCache = highestByClient.Values
                .OrderBy(summary => summary.ClientId, StringComparer.OrdinalIgnoreCase)
                .Select(ToSummary)
                .ToArray();
            return _summaryCache;
        }
        finally
        {
            _summaryIndexGate.Release();
        }
    }

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

    /// <summary>
    /// Consulta el historial íntegro de un cliente. Sólo la vista de monitor
    /// de Gerencia lo utiliza después de una búsqueda exacta; la consulta
    /// operativa de agentes conserva su límite compacto de visualización.
    /// </summary>
    public async Task<IReadOnlyList<CallDispositionEntry>> GetFullHistoryAsync(
        string? clientId,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientId = NormalizeClientId(clientId);
        if (normalizedClientId.Length == 0) return [];

        var gate = _clientGates.GetOrAdd(normalizedClientId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadFullHistoryLockedAsync(normalizedClientId, cancellationToken);
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
        string? channel,
        string? comment,
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

        var normalizedChannel = AvailableChannels.FirstOrDefault(available =>
            available.Equals(channel?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (normalizedChannel is null)
        {
            throw new InvalidOperationException("Selecciona el canal de la gestión.");
        }

        var normalizedComment = NormalizeComment(comment);
        if (CountWords(normalizedComment) > 100)
        {
            throw new InvalidOperationException("El comentario puede tener hasta 100 palabras.");
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
            normalizedChannel,
            outcome.Code,
            outcome.Weight,
            normalizedComment);

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

        try
        {
            await EnsureSummaryIndexAsync(cancellationToken);
            await AppendSummaryEventAsync(stored, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // El journal del cliente ya quedó persistido. Al borrar el índice
            // parcial, la siguiente consulta de Gerencia lo reconstruirá desde
            // esos journals sin perder la gestión recién registrada.
            _logger.LogError(exception, "No se pudo actualizar el índice de tipificaciones; se reconstruirá automáticamente.");
            try
            {
                await InvalidateSummaryIndexAsync();
            }
            catch (Exception invalidationException) when (invalidationException is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(invalidationException, "No se pudo invalidar el índice de tipificaciones después de un error.");
            }
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

    private async Task<IReadOnlyList<CallDispositionEntry>> ReadFullHistoryLockedAsync(
        string normalizedClientId,
        CancellationToken cancellationToken)
    {
        var path = ClientPath(normalizedClientId);
        if (!File.Exists(path)) return [];

        var entries = new List<CallDispositionEntry>();
        var skippedMalformedLine = false;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 8 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var stored = JsonSerializer.Deserialize<StoredCallDisposition>(line, SerializerOptions);
                if (stored is not null && stored.ClientId.Equals(normalizedClientId, StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(ToEntry(stored));
                }
            }
            catch (JsonException)
            {
                skippedMalformedLine = true;
            }
        }

        if (skippedMalformedLine)
        {
            _logger.LogWarning("Se ignoró una línea incompleta del historial íntegro de tipificaciones.");
        }

        return entries
            .OrderByDescending(entry => entry.RecordedAt)
            .ToArray();
    }

    private string ClientPath(string normalizedClientId) => Path.Combine(
        _storageDirectory,
        $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedClientId))).ToLowerInvariant()}.jsonl");

    private string SummaryIndexPath => Path.Combine(_storageDirectory, SummaryIndexFileName);

    private async Task EnsureSummaryIndexAsync(CancellationToken cancellationToken)
    {
        await _summaryIndexGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(SummaryIndexPath)) return;

            Directory.CreateDirectory(_storageDirectory);
            var temporaryPath = Path.Combine(_storageDirectory, $"summary-index-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 8 * 1024,
                    useAsync: true))
                {
                    await using var writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        bufferSize: 8 * 1024,
                        leaveOpen: true);
                    foreach (var path in Directory.EnumerateFiles(_storageDirectory, "*.jsonl")
                        .Where(path => !IsSummaryIndexFile(path)))
                    {
                        var summary = await ReadHighestSummaryFromClientFileAsync(path, cancellationToken);
                        if (summary is not null)
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(summary, SerializerOptions));
                        }
                    }

                    await writer.FlushAsync();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, SummaryIndexPath, overwrite: false);
                _summaryCache = null;
            }
            catch
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                throw;
            }
        }
        finally
        {
            _summaryIndexGate.Release();
        }
    }

    private async Task<StoredDispositionSummary?> ReadHighestSummaryFromClientFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        StoredDispositionSummary? highest = null;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 8 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var stored = JsonSerializer.Deserialize<StoredCallDisposition>(line, SerializerOptions);
                if (stored is null || string.IsNullOrWhiteSpace(stored.ClientId)) continue;
                var candidate = ToStoredSummary(stored);
                if (highest is null || IsHigher(candidate, highest)) highest = candidate;
            }
            catch (JsonException)
            {
                // La consulta individual ya tolera una última línea incompleta;
                // el índice aplica la misma regla al reconstruirse.
            }
        }

        return highest;
    }

    private async Task AppendSummaryEventAsync(StoredCallDisposition stored, CancellationToken cancellationToken)
    {
        await _summaryIndexGate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = new FileStream(
                SummaryIndexPath,
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
            await writer.WriteLineAsync(JsonSerializer.Serialize(ToStoredSummary(stored), SerializerOptions));
            await writer.FlushAsync();
            stream.Flush(flushToDisk: true);
            _summaryCache = null;
        }
        finally
        {
            _summaryIndexGate.Release();
        }
    }

    private async Task InvalidateSummaryIndexAsync()
    {
        await _summaryIndexGate.WaitAsync();
        try
        {
            _summaryCache = null;
            if (File.Exists(SummaryIndexPath)) File.Delete(SummaryIndexPath);
        }
        finally
        {
            _summaryIndexGate.Release();
        }
    }

    private static bool IsHigher(StoredDispositionSummary candidate, StoredDispositionSummary current) =>
        candidate.Weight > current.Weight
        || candidate.Weight == current.Weight && candidate.RecordedAt > current.RecordedAt;

    private static StoredDispositionSummary ToStoredSummary(StoredCallDisposition stored) => new(
        stored.ClientId,
        stored.RecordedAt,
        stored.AgentId,
        stored.AgentName,
        stored.Category,
        stored.Result,
        string.IsNullOrWhiteSpace(stored.Channel) ? "Sin canal" : stored.Channel,
        stored.Phone,
        stored.Weight);

    private static ClientDispositionSummary ToSummary(StoredDispositionSummary stored) => new(
        stored.ClientId,
        stored.RecordedAt,
        stored.AgentId,
        stored.AgentName,
        stored.Category,
        stored.Result,
        stored.Channel,
        stored.Phone);

    private static bool IsSummaryIndexFile(string path) => Path.GetFileName(path)
        .StartsWith("summary-index", StringComparison.OrdinalIgnoreCase);

    private static CallDispositionEntry ToEntry(StoredCallDisposition stored) => new(
        stored.Id,
        stored.RecordedAt,
        stored.ClientId,
        stored.AgentId,
        stored.AgentName,
        stored.Category,
        stored.Result,
        string.IsNullOrWhiteSpace(stored.Channel) ? "Sin canal" : stored.Channel,
        stored.Phone,
        stored.Comment ?? string.Empty);

    private static string NormalizeClientId(string? value) => (value ?? string.Empty).Trim();
    private static string NormalizeActorValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string NormalizeComment(string? value) => (value ?? string.Empty).Trim();
    private static int CountWords(string value) => string.IsNullOrWhiteSpace(value)
        ? 0
        : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

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
        string? Channel,
        string Code,
        int Weight,
        string? Comment);

    private sealed record StoredDispositionSummary(
        string ClientId,
        DateTimeOffset RecordedAt,
        string AgentId,
        string AgentName,
        string Category,
        string Result,
        string Channel,
        string Phone,
        int Weight);
}

public sealed class CallDispositionOptions
{
    public const string SectionName = "CallDispositions";
    public string StoragePath { get; init; } = "/var/data/ascendra-call-dispositions";
    public int HistoryDisplayLimit { get; init; } = 80;
}
