using Ascendra.Agents.Web.Domain;
using Ascendra.Agents.Web.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Ascendra.Agents.Web.Services;

/// <summary>
/// Registra promesas capturadas por agentes. La cartera y el nombre del
/// cliente se validan contra el mismo corte que utiliza Búsqueda clientes.
/// </summary>
public sealed class PromiseRegistrationService(
    ExcelSnapshotStore snapshots,
    PostgresDatabase database,
    ILogger<PromiseRegistrationService> logger)
{
    private static readonly IReadOnlyList<string> AvailablePromiseTypes =
        ["Liquidación", "Parcial", "Plan de Pago", "Recurrencia", "RMD"];

    private readonly ExcelSnapshotStore _snapshots = snapshots;
    private readonly PostgresDatabase _database = database;
    private readonly ILogger<PromiseRegistrationService> _logger = logger;
    private readonly SemaphoreSlim _reconciliationSyncGate = new(1, 1);
    private string? _lastReconciledSnapshotVersion;

    public event Action? Changed;

    public IReadOnlyList<string> Types => AvailablePromiseTypes;

    public async Task<PromiseClientLookup> LookupClientAsync(
        string? clientId,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientId = NormalizeClientId(clientId);
        var client = FindClient(normalizedClientId);
        if (client is null)
        {
            return new PromiseClientLookup(normalizedClientId, string.Empty, false, null, []);
        }

        var activePromise = FindLegacyActivePromise(normalizedClientId)
            ?? await FindStoredActivePromiseAsync(normalizedClientId, cancellationToken);
        return new PromiseClientLookup(normalizedClientId, client.Name, true, activePromise, client.ContactNumbers);
    }

    public async Task<PromiseRegistrationEntry> RegisterAsync(
        string? clientId,
        string? clientNumber,
        DateOnly? promiseDate,
        decimal? initialAmount,
        decimal? weeklyAmount,
        int numberOfWeeks,
        string? promiseType,
        string? agentName,
        string? agentId,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientId = NormalizeClientId(clientId);
        var clientName = FindClient(normalizedClientId)?.Name
            ?? throw new InvalidOperationException("El CLIENTE_UNICO no existe en la cartera vigente.");
        var normalizedPhone = TextNormalization.Phone(clientNumber);
        if (normalizedPhone.Length != 10)
        {
            throw new InvalidOperationException("Ingresa un número contactado válido de 10 dígitos.");
        }
        if (promiseDate is null)
        {
            throw new InvalidOperationException("Selecciona la fecha de la promesa.");
        }
        if (initialAmount is null || initialAmount < 0 || weeklyAmount is null || weeklyAmount < 0)
        {
            throw new InvalidOperationException("Captura montos válidos iguales o mayores a cero.");
        }
        if (numberOfWeeks is < 0 or > 255)
        {
            throw new InvalidOperationException("El número de semanas debe estar entre 0 y 255.");
        }

        var normalizedType = AvailablePromiseTypes.FirstOrDefault(type =>
            type.Equals(promiseType?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (normalizedType is null)
        {
            throw new InvalidOperationException("Selecciona un tipo de promesa válido.");
        }

        var normalizedAgentName = NormalizeRequired(agentName, "Agente Ascendra");
        var normalizedAgentId = NormalizeRequired(agentId, "Sin ID");
        var legacyActivePromise = FindLegacyActivePromise(normalizedClientId);
        if (legacyActivePromise is not null)
        {
            throw ActivePromiseException(legacyActivePromise);
        }

        var createdAt = DateTimeOffset.UtcNow;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Evita que dos agentes registren simultáneamente promesas vigentes
            // para el mismo cliente aunque ninguna fila previa exista todavía.
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext(@clientId));", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("clientId", normalizedClientId);
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var activePromise = await FindStoredActivePromiseAsync(
                normalizedClientId, connection, transaction, cancellationToken);
            if (activePromise is not null)
            {
                throw ActivePromiseException(activePromise);
            }

            await using var insert = new NpgsqlCommand("""
                INSERT INTO promesas (
                    cliente_unico, agente, numero_cliente, fecha_promesa,
                    monto_inicial, monto_semanal, tipo_promesa, numero_semanas,
                    fecha_creacion, usu_alta)
                VALUES (
                    @clientId, @agentName, @clientNumber, @promiseDate,
                    @initialAmount, @weeklyAmount, @promiseType, @numberOfWeeks,
                    @createdAt, @agentId)
                RETURNING id_promesa;
                """, connection, transaction);
            insert.Parameters.AddWithValue("clientId", normalizedClientId);
            insert.Parameters.AddWithValue("agentName", normalizedAgentName);
            insert.Parameters.AddWithValue("clientNumber", normalizedPhone);
            insert.Parameters.AddWithValue("promiseDate", promiseDate.Value);
            insert.Parameters.Add(new NpgsqlParameter("initialAmount", NpgsqlDbType.Numeric) { Value = initialAmount.Value });
            insert.Parameters.Add(new NpgsqlParameter("weeklyAmount", NpgsqlDbType.Numeric) { Value = weeklyAmount.Value });
            insert.Parameters.AddWithValue("promiseType", normalizedType);
            insert.Parameters.AddWithValue("numberOfWeeks", checked((short)numberOfWeeks));
            insert.Parameters.AddWithValue("createdAt", createdAt);
            insert.Parameters.AddWithValue("agentId", normalizedAgentId);
            var idPromise = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            var entry = new PromiseRegistrationEntry(
                idPromise, normalizedClientId, clientName, normalizedAgentName,
                normalizedPhone, promiseDate.Value, initialAmount, weeklyAmount,
                normalizedType, numberOfWeeks, createdAt, normalizedAgentId);
            NotifyChanged();
            return entry;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or TimeoutException)
        {
            _logger.LogError(exception, "No se pudo registrar una promesa en PostgreSQL.");
            throw new InvalidOperationException("No se pudo guardar la promesa. Intenta nuevamente.");
        }
    }

    /// <summary>
    /// Registros que todavía no vienen identificados en el corte de promesas.
    /// Al estar presentes en el Excel con IdPromesa_Ascendra, el corte se
    /// vuelve la única versión visible y esta fila deja de duplicarse.
    /// </summary>
    public async Task<IReadOnlyList<PromiseRegistrationEntry>> GetPendingForAgentAsync(
        string? agentId,
        CancellationToken cancellationToken = default) =>
        FilterReconciled(await GetEntriesAsync(
            "WHERE origen = 'Ascendra' AND usu_alta = @agentId", "agentId", NormalizeRequired(agentId, "Sin ID"), cancellationToken));

    public async Task<IReadOnlyList<PromiseRegistrationEntry>> GetPendingForTeamAsync(
        CancellationToken cancellationToken = default) =>
        FilterReconciled(await GetEntriesAsync("WHERE origen = 'Ascendra'", null, null, cancellationToken));

    /// <summary>
    /// Fuente de la pestaña Equipo → Promesas. Antes de leer, replica al
    /// registro operativo el estatus calculado por el último cruce válido.
    /// Así Gerencia, Monitoreo y los agentes comparten el mismo resultado.
    /// </summary>
    public async Task<IReadOnlyList<DatabasePromiseEntry>> GetAllForTeamAsync(
        CancellationToken cancellationToken = default)
    {
        await SynchronizeReconciledStatusesAsync(cancellationToken);
        var (reconciledStatuses, _) = ReconciledStatuses(_snapshots.Current);
        var entries = new List<DatabasePromiseEntry>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id_promesa, cliente_unico, agente, usu_alta, numero_cliente,
                   fecha_promesa, monto_inicial, monto_semanal, tipo_promesa,
                   numero_semanas, fecha_creacion, estatus, origen,
                   modificado_por, fecha_modificacion
            FROM promesas
            ORDER BY fecha_creacion DESC, id_promesa DESC;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entry = ReadDatabasePromiseEntry(reader);
            // Si PostgreSQL no estuvo disponible durante la sincronización,
            // la consola sigue mostrando el resultado autoritativo del corte.
            if (entry.Origin.Equals("Ascendra", StringComparison.OrdinalIgnoreCase)
                && reconciledStatuses.TryGetValue(entry.IdPromise, out var reconciledStatus))
            {
                entry = entry with { Status = reconciledStatus };
            }
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Copia una vez por corte los estatus calculados por el proceso de
    /// cruces. Sólo toca altas de Ascendra enlazadas mediante
    /// IdPromesa_Ascendra; el histórico y las modificaciones administrativas
    /// permanecen intactos.
    /// </summary>
    public async Task SynchronizeReconciledStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshots.Current;
        if (snapshot == AgentDataSnapshot.Empty
            || string.Equals(Volatile.Read(ref _lastReconciledSnapshotVersion), snapshot.Version, StringComparison.Ordinal))
        {
            return;
        }

        await _reconciliationSyncGate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _snapshots.Current;
            if (snapshot == AgentDataSnapshot.Empty
                || string.Equals(Volatile.Read(ref _lastReconciledSnapshotVersion), snapshot.Version, StringComparison.Ordinal))
            {
                return;
            }

            var (statuses, conflictingIds) = ReconciledStatuses(snapshot);
            if (conflictingIds > 0)
            {
                _logger.LogWarning(
                    "El corte contiene {ConflictingIds} IdPromesa_Ascendra con estatus contradictorios; se omitieron de la sincronización.",
                    conflictingIds);
            }

            if (statuses.Count == 0)
            {
                Volatile.Write(ref _lastReconciledSnapshotVersion, snapshot.Version);
                return;
            }

            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var update = new NpgsqlCommand("""
                UPDATE promesas
                SET estatus = @status
                WHERE id_promesa = @idPromise
                  AND origen = 'Ascendra'
                  AND estatus IS DISTINCT FROM @status;
                """, connection, transaction);
            var idParameter = update.Parameters.Add("idPromise", NpgsqlDbType.Integer);
            var statusParameter = update.Parameters.Add("status", NpgsqlDbType.Text);
            var updatedCount = 0;
            foreach (var (idPromise, status) in statuses.OrderBy(pair => pair.Key))
            {
                idParameter.Value = idPromise;
                statusParameter.Value = status;
                updatedCount += await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);

            Volatile.Write(ref _lastReconciledSnapshotVersion, snapshot.Version);
            if (updatedCount > 0)
            {
                _logger.LogInformation(
                    "Se sincronizaron {UpdatedCount} estatus de promesas desde el corte reconciliado de Ascendra.",
                    updatedCount);
                NotifyChanged();
            }
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or TimeoutException)
        {
            // No sustituye el corte en memoria ni impide que Supervisión vea
            // el estatus correcto mientras PostgreSQL se recupera.
            _logger.LogError(exception, "No se pudieron sincronizar los estatus reconciliados de promesas en PostgreSQL.");
        }
        finally
        {
            _reconciliationSyncGate.Release();
        }
    }

    /// <summary>
    /// Corrección exclusiva de Gerencia y Dirección. La reasignación actualiza
    /// tanto el nombre mostrado como UsuAlta, para conservar el vínculo del
    /// registro con el agente activo seleccionado.
    /// </summary>
    public async Task<DatabasePromiseEntry> UpdateAsync(
        PromiseEditRequest request,
        AgentUser editor,
        CancellationToken cancellationToken = default)
    {
        if (!editor.CanManagePromises)
        {
            throw new InvalidOperationException("No tienes permiso para modificar promesas.");
        }
        if (request.IdPromise <= 0)
        {
            throw new InvalidOperationException("Selecciona una promesa válida.");
        }

        var clientId = NormalizeClientId(request.ClientId);
        if (FindClient(clientId) is null)
        {
            throw new InvalidOperationException("El CLIENTE_UNICO no existe en la cartera vigente.");
        }
        var phone = TextNormalization.Phone(request.ClientNumber);
        if (phone.Length != 10)
        {
            throw new InvalidOperationException("Ingresa un número contactado válido de 10 dígitos.");
        }
        if (request.InitialAmount is < 0 || request.WeeklyAmount is < 0)
        {
            throw new InvalidOperationException("Los montos no pueden ser negativos.");
        }
        if (request.NumberOfWeeks is < 0 or > 255)
        {
            throw new InvalidOperationException("El número de semanas debe estar entre 0 y 255.");
        }
        var promiseType = AvailablePromiseTypes.FirstOrDefault(type =>
            type.Equals(request.PromiseType?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (promiseType is null)
        {
            throw new InvalidOperationException("Selecciona un tipo de promesa válido.");
        }
        var assignee = _snapshots.Current.Users.FirstOrDefault(user =>
            user.IsActive && user.IsAgent
            && user.Id.Equals(request.AssignedAgentId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (assignee is null)
        {
            throw new InvalidOperationException("Selecciona un agente activo para reasignar la promesa.");
        }

        var createdAt = OperationalClock.FromMexicoCityLocal(request.CreatedAtLocal).ToUniversalTime();
        var modifiedAt = DateTimeOffset.UtcNow;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var history = new NpgsqlCommand("""
                INSERT INTO promesas_historial (
                    id_promesa, accion, registrado_en, registrado_por,
                    datos_anteriores, datos_nuevos)
                SELECT p.id_promesa, 'Modificación', @modifiedAt, @modifiedBy,
                       to_jsonb(p),
                       jsonb_build_object(
                           'cliente_unico', @clientId,
                           'agente', @agentName,
                           'usu_alta', @agentId,
                           'numero_cliente', @clientNumber,
                           'fecha_promesa', @promiseDate,
                           'monto_inicial', @initialAmount,
                           'monto_semanal', @weeklyAmount,
                           'tipo_promesa', @promiseType,
                           'numero_semanas', @numberOfWeeks,
                           'fecha_creacion', @createdAt,
                           'modificado_por', @modifiedBy,
                           'fecha_modificacion', @modifiedAt)
                FROM promesas AS p
                WHERE p.id_promesa = @idPromise;
                """, connection, transaction))
            {
                AddEditParameters(history, request.IdPromise, clientId, assignee, phone, request,
                    promiseType, createdAt, editor.DisplayName, modifiedAt);
                if (await history.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new InvalidOperationException("La promesa ya no existe.");
                }
            }

            DatabasePromiseEntry updated;
            await using (var command = new NpgsqlCommand("""
                UPDATE promesas
                SET cliente_unico = @clientId,
                    agente = @agentName,
                    usu_alta = @agentId,
                    numero_cliente = @clientNumber,
                    fecha_promesa = @promiseDate,
                    monto_inicial = @initialAmount,
                    monto_semanal = @weeklyAmount,
                    tipo_promesa = @promiseType,
                    numero_semanas = @numberOfWeeks,
                    fecha_creacion = @createdAt,
                    modificado_por = @modifiedBy,
                    fecha_modificacion = @modifiedAt
                WHERE id_promesa = @idPromise
                RETURNING id_promesa, cliente_unico, agente, usu_alta, numero_cliente,
                          fecha_promesa, monto_inicial, monto_semanal, tipo_promesa,
                          numero_semanas, fecha_creacion, estatus, origen,
                          modificado_por, fecha_modificacion;
                """, connection, transaction))
            {
                AddEditParameters(command, request.IdPromise, clientId, assignee, phone, request,
                    promiseType, createdAt, editor.DisplayName, modifiedAt);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("La promesa ya no existe.");
                }
                updated = ReadDatabasePromiseEntry(reader);
            }
            await transaction.CommitAsync(cancellationToken);
            NotifyChanged();
            return updated;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or TimeoutException)
        {
            _logger.LogError(exception, "No se pudo modificar la promesa {PromiseId} en PostgreSQL.", request.IdPromise);
            throw new InvalidOperationException("No se pudo modificar la promesa. Intenta nuevamente.");
        }
    }

    /// <summary>Elimina una promesa sin perder su evidencia administrativa.</summary>
    public async Task DeleteAsync(
        int idPromise,
        AgentUser editor,
        CancellationToken cancellationToken = default)
    {
        if (!editor.CanManagePromises)
        {
            throw new InvalidOperationException("No tienes permiso para eliminar promesas.");
        }
        if (idPromise <= 0)
        {
            throw new InvalidOperationException("Selecciona una promesa válida.");
        }

        var deletedAt = DateTimeOffset.UtcNow;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var history = new NpgsqlCommand("""
                INSERT INTO promesas_historial (
                    id_promesa, accion, registrado_en, registrado_por,
                    datos_anteriores, datos_nuevos)
                SELECT p.id_promesa, 'Eliminación', @deletedAt, @deletedBy,
                       to_jsonb(p), NULL
                FROM promesas AS p
                WHERE p.id_promesa = @idPromise;
                """, connection, transaction))
            {
                history.Parameters.AddWithValue("idPromise", idPromise);
                history.Parameters.AddWithValue("deletedAt", deletedAt);
                history.Parameters.AddWithValue("deletedBy", editor.DisplayName);
                if (await history.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new InvalidOperationException("La promesa ya no existe.");
                }
            }
            await using (var delete = new NpgsqlCommand(
                "DELETE FROM promesas WHERE id_promesa = @idPromise;", connection, transaction))
            {
                delete.Parameters.AddWithValue("idPromise", idPromise);
                if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException("La promesa ya no existe.");
                }
            }
            await transaction.CommitAsync(cancellationToken);
            NotifyChanged();
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or TimeoutException)
        {
            _logger.LogError(exception, "No se pudo eliminar la promesa {PromiseId} en PostgreSQL.", idPromise);
            throw new InvalidOperationException("No se pudo eliminar la promesa. Intenta nuevamente.");
        }
    }

    public async Task<IReadOnlyList<PromiseCrossSourceEntry>> GetCrossSourceAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = new List<PromiseCrossSourceEntry>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id_promesa, id_promesa_origen, origen, cliente_unico,
                   numero_cliente, agente, usu_alta, fecha_promesa,
                   monto_inicial, monto_semanal, tipo_promesa, numero_semanas,
                   fecha_creacion
            FROM promesas
            ORDER BY fecha_creacion, id_promesa;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var registrationId = reader.GetInt32(0);
            var origin = reader.IsDBNull(2) ? "Ascendra" : reader.GetString(2);
            var historicalId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var isAscendra = origin.Equals("Ascendra", StringComparison.OrdinalIgnoreCase);
            entries.Add(new PromiseCrossSourceEntry(
                !isAscendra && !string.IsNullOrWhiteSpace(historicalId)
                    ? historicalId
                    : $"ASC-{registrationId:D6}",
                isAscendra ? registrationId : null,
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateOnly>(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt16(11),
                reader.GetFieldValue<DateTimeOffset>(12)));
        }
        return entries;
    }

    /// <summary>
    /// El cruce conserva el estatus y el nombre provenientes del Excel, pero
    /// los campos administrativos editables se resuelven siempre desde
    /// PostgreSQL. Así un cambio realizado por Gerencia se refleja en Mis
    /// promesas de inmediato, aun antes del siguiente cruce.
    /// </summary>
    public static IReadOnlyList<PromiseRecord> ApplyStoredEdits(
        IReadOnlyList<PromiseRecord> snapshotPromises,
        IReadOnlyList<PromiseCrossSourceEntry> storedPromises)
    {
        var storedByKey = storedPromises
            .GroupBy(StoredPromiseKey)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.CreatedAt).First(),
                StringComparer.Ordinal);

        return snapshotPromises.Select(snapshot =>
        {
            if (!storedByKey.TryGetValue(SnapshotPromiseKey(snapshot), out var stored)) return snapshot;

            return snapshot with
            {
                ClientId = stored.ClientId,
                ClientNumber = stored.ClientNumber,
                Agent = string.IsNullOrWhiteSpace(stored.AgentName) ? snapshot.Agent : stored.AgentName,
                AgentId = string.IsNullOrWhiteSpace(stored.AgentId) ? snapshot.AgentId : stored.AgentId,
                PromiseDate = stored.PromiseDate,
                InitialAmount = stored.InitialAmount ?? snapshot.InitialAmount,
                WeeklyAmount = stored.WeeklyAmount ?? snapshot.WeeklyAmount,
                Type = string.IsNullOrWhiteSpace(stored.PromiseType) ? snapshot.Type : stored.PromiseType,
                NumberOfWeeks = stored.NumberOfWeeks,
                CreatedOn = OperationalClock.Date(stored.CreatedAt),
            };
        }).ToArray();
    }

    private static string SnapshotPromiseKey(PromiseRecord promise) =>
        promise.AscendraRegistrationId is int registrationId
            ? $"ascendra:{registrationId}"
            : $"historial:{promise.Id.Trim()}";

    private static string StoredPromiseKey(PromiseCrossSourceEntry promise) =>
        promise.AscendraRegistrationId is int registrationId
            ? $"ascendra:{registrationId}"
            : $"historial:{promise.PromiseId.Trim()}";

    private async Task<IReadOnlyList<PromiseRegistrationEntry>> GetEntriesAsync(
        string whereClause,
        string? parameterName,
        string? parameterValue,
        CancellationToken cancellationToken)
    {
        var entries = new List<PromiseRegistrationEntry>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT id_promesa, cliente_unico, agente, numero_cliente, fecha_promesa,
                   monto_inicial, monto_semanal, tipo_promesa, numero_semanas,
                   fecha_creacion, usu_alta
            FROM promesas
            {whereClause}
            ORDER BY fecha_creacion DESC, id_promesa DESC
            LIMIT 500;
            """, connection);
        if (parameterName is not null && parameterValue is not null)
        {
            command.Parameters.AddWithValue(parameterName, parameterValue);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var clientId = reader.GetString(1);
            entries.Add(new PromiseRegistrationEntry(
                reader.GetInt32(0),
                clientId,
                FindClient(clientId)?.Name ?? "Nombre no disponible en cartera vigente",
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateOnly>(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.GetString(7),
                reader.GetInt16(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetString(10)));
        }
        return entries;
    }

    private async Task<PromiseActiveLock?> FindStoredActivePromiseAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        return await FindStoredActivePromiseAsync(clientId, connection, null, cancellationToken);
    }

    private async Task<PromiseActiveLock?> FindStoredActivePromiseAsync(
        string clientId,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var today = OperationalClock.Date(DateTimeOffset.UtcNow);
        await using var command = new NpgsqlCommand("""
            SELECT id_promesa, fecha_promesa, estatus
            FROM promesas
            WHERE origen = 'Ascendra' AND cliente_unico = @clientId AND fecha_promesa >= @today
            ORDER BY fecha_promesa ASC, id_promesa ASC
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("clientId", clientId);
        command.Parameters.AddWithValue("today", today);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var registrationId = reader.GetInt32(0);
            var status = reader.IsDBNull(2) ? "Pendiente de pago" : reader.GetString(2);
            // Si el corte ya trae esta promesa, el estatus del corte es la
            // autoridad. Las capturas todavía pendientes permiten otra alta.
            if (IsIncludedInCurrentCut(registrationId)) continue;
            if (!BlocksNewRegistration(status)) continue;

            return new PromiseActiveLock(
                registrationId.ToString("D2"),
                reader.GetFieldValue<DateOnly>(1),
                "Ascendra · revisar estatus");
        }
        return null;
    }

    private PromiseActiveLock? FindLegacyActivePromise(string clientId)
    {
        var today = OperationalClock.Date(DateTimeOffset.UtcNow);
        var promises = _snapshots.Current.PromisesByClientId.TryGetValue(clientId, out var entries)
            ? entries
            : [];
        var entry = promises
            .Where(promise => promise.ClientId.Equals(clientId, StringComparison.OrdinalIgnoreCase)
                && promise.PromiseDate is not null
                && promise.PromiseDate >= today
                && BlocksNewRegistration(promise.Status))
            .OrderBy(promise => promise.PromiseDate)
            .ThenBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return entry is null
            ? null
            : new PromiseActiveLock(entry.Id, entry.PromiseDate!.Value, "corte · revisar estatus");
    }

    // Activa y Vigente (mostrada como Esperando pago) permiten una nueva
    // captura. Los estatus conocidos que ya cerraron la promesa tampoco
    // bloquean; sólo un estatus futuro no reconocido queda protegido.
    private static bool BlocksNewRegistration(string? status) =>
        TextNormalization.Normalize(status) is not (
            "activa" or "vigente" or "esperando pago" or "pendiente de pago"
            or "incumplida" or "duplicada" or "robada");

    private static (IReadOnlyDictionary<int, string> Statuses, int ConflictingIds) ReconciledStatuses(
        AgentDataSnapshot snapshot)
    {
        var statuses = new Dictionary<int, string>();
        var conflictingIds = 0;
        foreach (var group in snapshot.Promises
                     .Where(promise => promise.AscendraRegistrationId is not null)
                     .GroupBy(promise => promise.AscendraRegistrationId!.Value))
        {
            var candidates = group
                .Select(promise => promise.Status?.Trim())
                .Where(status => !string.IsNullOrWhiteSpace(status))
                .GroupBy(TextNormalization.Normalize, StringComparer.Ordinal)
                .ToArray();

            if (candidates.Length == 0) continue;
            if (candidates.Length > 1)
            {
                conflictingIds++;
                continue;
            }

            statuses[group.Key] = candidates[0].First()!;
        }
        return (statuses, conflictingIds);
    }

    private IReadOnlyList<PromiseRegistrationEntry> FilterReconciled(
        IReadOnlyList<PromiseRegistrationEntry> entries) =>
        entries.Where(entry => !IsIncludedInCurrentCut(entry.IdPromise)).ToArray();

    private bool IsIncludedInCurrentCut(int registrationId) =>
        _snapshots.Current.AscendraRegistrationIds.Contains(registrationId);

    private DatabasePromiseEntry ReadDatabasePromiseEntry(NpgsqlDataReader reader)
    {
        var clientId = reader.GetString(1);
        return new DatabasePromiseEntry(
            reader.GetInt32(0),
            clientId,
            FindClient(clientId)?.Name ?? "Nombre no disponible en cartera vigente",
            reader.IsDBNull(2) ? "Sin agente" : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateOnly>(5),
            reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.IsDBNull(8) ? "Sin tipo" : reader.GetString(8),
            reader.GetInt16(9),
            reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? "Pendiente de pago" : reader.GetString(11),
            reader.IsDBNull(12) ? "Ascendra" : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14));
    }

    private ClientMatch? FindClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;

        var snapshot = _snapshots.Current;
        var contacts = snapshot.ContactsByClientId.TryGetValue(clientId, out var matchedContacts)
            ? matchedContacts
            : [];
        var portfolio = snapshot.PortfolioByClientId.TryGetValue(clientId, out var portfolioRecord)
            ? portfolioRecord
            : null;
        if (contacts.Count == 0 && portfolio is null) return null;

        var name = contacts
            .Select(contact => contact.ClientName)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim() ?? portfolio?.ClientName?.Trim();

        var sourcesByPhone = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void AddPhone(string? phone, string source)
        {
            var normalized = TextNormalization.Phone(phone);
            if (normalized.Length != 10) return;
            if (!sourcesByPhone.TryGetValue(normalized, out var sources))
            {
                sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                sourcesByPhone[normalized] = sources;
            }
            sources.Add(source);
        }

        foreach (var phone in portfolio?.Phones ?? []) AddPhone(phone, "Cartera");
        foreach (var contact in contacts)
        {
            var source = string.IsNullOrWhiteSpace(contact.ReferenceName)
                ? "Cartera"
                : "Referencia";
            AddPhone(contact.Phone, source);
        }

        var contactNumbers = sourcesByPhone
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new PromiseContactNumber(
                pair.Key,
                string.Join(" · ", pair.Value.OrderBy(source => source, StringComparer.OrdinalIgnoreCase))))
            .ToArray();

        return new ClientMatch(
            string.IsNullOrWhiteSpace(name) ? "Nombre no disponible en cartera vigente" : name,
            contactNumbers);
    }

    private static string NormalizeClientId(string? value) => (value ?? string.Empty).Trim();

    private static void AddEditParameters(
        NpgsqlCommand command,
        int idPromise,
        string clientId,
        AgentUser assignee,
        string phone,
        PromiseEditRequest request,
        string promiseType,
        DateTimeOffset createdAt,
        string modifiedBy,
        DateTimeOffset modifiedAt)
    {
        command.Parameters.AddWithValue("idPromise", idPromise);
        command.Parameters.AddWithValue("clientId", clientId);
        command.Parameters.AddWithValue("agentName", assignee.DisplayName);
        command.Parameters.AddWithValue("agentId", assignee.Id);
        command.Parameters.AddWithValue("clientNumber", phone);
        command.Parameters.AddWithValue("promiseDate", request.PromiseDate);
        command.Parameters.Add(new NpgsqlParameter("initialAmount", NpgsqlDbType.Numeric) { Value = request.InitialAmount ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("weeklyAmount", NpgsqlDbType.Numeric) { Value = request.WeeklyAmount ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("promiseType", promiseType);
        command.Parameters.AddWithValue("numberOfWeeks", checked((short)request.NumberOfWeeks));
        command.Parameters.AddWithValue("createdAt", createdAt);
        command.Parameters.AddWithValue("modifiedBy", modifiedBy);
        command.Parameters.AddWithValue("modifiedAt", modifiedAt);
    }

    private static string NormalizeRequired(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static InvalidOperationException ActivePromiseException(PromiseActiveLock activePromise) =>
        new($"Este cliente ya tiene una promesa vigente ({activePromise.PromiseId}) para el {activePromise.PromiseDate:dd/MM/yyyy}.");

    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch { /* Una vista cerrada no debe impedir el registro. */ }
        }
    }

    private sealed record ClientMatch(
        string Name,
        IReadOnlyList<PromiseContactNumber> ContactNumbers);
}
