using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Ascendra.Agents.Web.Domain;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ascendra.Agents.Web.Infrastructure;

/// <summary>
/// Conserva una vista en memoria de los archivos todavía pendientes de migración.
/// Al activar AgentDirectory:UsePostgres, la identidad procede de PostgreSQL.
/// </summary>
public sealed class ExcelSnapshotStore(
    IOptionsMonitor<AgentDataOptions> options,
    IHostEnvironment environment,
    AgentDirectoryService agentDirectory,
    IOptions<AgentDirectoryOptions> directoryOptions,
    ILogger<ExcelSnapshotStore> logger)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AgentDataSnapshot _current = AgentDataSnapshot.Empty;
    private DataHealth _health = new(false, "Estamos preparando el corte de trabajo…", null, "pending");
    private string? _lastFingerprint;

    /// <summary>
    /// Se emite sólo después de reemplazar un corte válido. Las pantallas
    /// interactivas se suscriben para no quedarse mostrando cifras del Excel
    /// anterior mientras siguen abiertas.
    /// </summary>
    public event Action? Changed;

    public AgentDataSnapshot Current => Volatile.Read(ref _current);
    public DataHealth Health => Volatile.Read(ref _health);
    public int RefreshSeconds => Math.Clamp(options.CurrentValue.RefreshSeconds, 15, 900);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = Paths();
            try
            {
                var directory = directoryOptions.Value.UsePostgres
                    ? await agentDirectory.GetAsync(cancellationToken) : null;
                var fingerprint = $"{Fingerprint(paths, directory is not null)}:{directory?.Version ?? "excel"}";
                if (fingerprint == _lastFingerprint)
                {
                    Volatile.Write(ref _health, new DataHealth(true, "Corte disponible.", Current.LoadedAt, Current.Version));
                    return;
                }
                var snapshot = Load(paths, fingerprint, directory);
                Volatile.Write(ref _current, snapshot);
                Volatile.Write(ref _health, new DataHealth(
                    directory is not null || snapshot.Users.Count > 0,
                    directory is not null || snapshot.Users.Count > 0
                        ? $"Corte cargado · {snapshot.LoadedAt:dd/MM/yyyy HH:mm}"
                        : "Falta headcount.xlsx para habilitar el acceso.",
                    snapshot.LoadedAt,
                    snapshot.Version));
                _lastFingerprint = fingerprint;
                logger.LogInformation("Corte de Agentes actualizado. Pagos: {Payments}; promesas: {Promises}; contactos: {Contacts}.",
                    snapshot.Payments.Count, snapshot.Promises.Count, snapshot.Contacts.Count);
                Changed?.Invoke();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or NpgsqlException or TimeoutException)
            {
                var current = Current;
                var message = current == AgentDataSnapshot.Empty
                    ? $"No se pudo cargar el corte: {exception.Message}"
                    : "El nuevo corte tiene un problema; se conserva el último corte válido.";
                Volatile.Write(ref _health, new DataHealth(!directoryOptions.Value.UsePostgres && current.Users.Count > 0,
                    message, current.LoadedAt == DateTimeOffset.MinValue ? null : current.LoadedAt, current.Version));
                logger.LogError(exception, "No se pudo actualizar el corte de Agentes.");
            }
        }
        finally { _refreshLock.Release(); }
    }

    private AgentDataSnapshot Load(DataPaths paths, string fingerprint, AgentDirectorySnapshot? directory)
    {
        var warnings = new List<string>();
        var headcount = directory is null
            ? ReadHeadcount(paths.Input("headcount.xlsx"), warnings)
            : new HeadcountRoster(directory.Users, directory.Directory);
        var catalog = new AgentIdentityCatalog(headcount.Directory);
        var loadedPayments = ReadOptional(paths.Input("agents_collections.xlsx"), ReadPayments, warnings)
            .Select(catalog.Canonicalize)
            .ToArray();
        // Se separa la cobranza orgánica para que sólo la consola de equipo
        // pueda consultarla: nunca se mezcla con la vista personal del agente.
        var payments = loadedPayments.Where(x => !IsOrganicPayment(x)).ToArray();
        var organicPayments = loadedPayments.Where(IsOrganicPayment).ToArray();
        var promises = ReadOptional(paths.Input("promesas_estatus.xlsx"), ReadPromises, warnings)
            .Select(catalog.Canonicalize)
            .Where(x => !TextNormalization.IsOrganic(x.Agent)).ToArray();
        var portfolio = ReadOptional(paths.Input("Descarga_cartera.xlsx"), ReadPortfolio, warnings);
        var contacts = ReadContacts(paths, portfolio, warnings);
        return new AgentDataSnapshot(
            fingerprint,
            DateTimeOffset.Now,
            headcount.Users,
            payments,
            organicPayments,
            promises,
            portfolio,
            contacts,
            headcount.Directory,
            warnings);
    }

    private static bool IsOrganicPayment(PaymentRecord payment) =>
        TextNormalization.IsOrganic(payment.Agent)
        || TextNormalization.IsOrganic(payment.AssignmentType);

    private static IReadOnlyList<T> ReadOptional<T>(string path, Func<string, IReadOnlyList<T>> read, ICollection<string> warnings)
    {
        if (!File.Exists(path))
        {
            warnings.Add($"Falta {Path.GetFileName(path)}.");
            return Array.Empty<T>();
        }
        return read(path);
    }

    private static HeadcountRoster ReadHeadcount(string path, ICollection<string> warnings)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException("Falta headcount.xlsx.");
        }

        var workbook = WorkbookTable.Read(path);
        var requiredHeaders = new[]
        {
            "id", "a_paterno", "a_materno", "nombre", "usuario", "nombre_visible", "estado", "rol",
        };
        var missingHeaders = requiredHeaders.Where(header => !workbook.HasHeader(header)).ToArray();
        if (missingHeaders.Length > 0)
        {
            throw new InvalidDataException($"headcount.xlsx no contiene: {string.Join(", ", missingHeaders)}.");
        }

        var directory = workbook.Rows
            .Select(row =>
            {
                var rawRole = row.Text("rol");
                var active = IsActive(row.Text("estado"));
                if (active && !IsKnownRole(rawRole))
                {
                    if (TextNormalization.Normalize(rawRole) == "activo")
                    {
                        warnings.Add($"El rol 'Activo' se interpretó como Agente para el ID {row.Text("id")}.");
                    }
                    else
                    {
                        throw new InvalidDataException($"El ID {row.Text("id")} tiene un rol activo no reconocido.");
                    }
                }

                return new AgentDirectoryRecord(
                    TextNormalization.AgentId(row.Text("id")),
                    row.Text("nombre"),
                    row.Text("a_paterno", "apaterno"),
                    row.Text("a_materno", "amaterno"),
                    row.Text("nombre_visible"),
                    TextNormalization.Normalize(row.Text("usuario")),
                    NormaliseRole(rawRole),
                    active);
            })
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .ToArray();

        if (directory.Length == 0)
        {
            throw new InvalidDataException("headcount.xlsx no contiene personas.");
        }

        var missingIdentity = directory.FirstOrDefault(agent =>
            string.IsNullOrWhiteSpace(agent.Id)
            || string.IsNullOrWhiteSpace(agent.FirstName)
            || string.IsNullOrWhiteSpace(agent.PaternalSurname)
            || string.IsNullOrWhiteSpace(agent.DisplayName));
        if (missingIdentity is not null)
        {
            throw new InvalidDataException("headcount.xlsx contiene una persona sin ID, nombre, apellido paterno o nombre visible.");
        }

        var duplicatedIds = directory
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatedIds.Length > 0)
        {
            throw new InvalidDataException($"headcount.xlsx contiene IDs duplicados: {string.Join(", ", duplicatedIds)}.");
        }

        var activeAgents = directory.Where(agent => agent.IsActive).ToArray();
        var incompleteActive = activeAgents.FirstOrDefault(agent =>
            IsPlaceholder(agent.Username) || IsPlaceholder(agent.DisplayName));
        if (incompleteActive is not null)
        {
            throw new InvalidDataException($"El ID {incompleteActive.Id} está activo sin usuario o nombre visible válido.");
        }

        var duplicatedUsernames = activeAgents
            .GroupBy(agent => agent.Username, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatedUsernames.Length > 0)
        {
            throw new InvalidDataException($"headcount.xlsx contiene usuarios activos duplicados: {string.Join(", ", duplicatedUsernames)}.");
        }

        var users = activeAgents
            .Select(agent => new AgentUser(
                agent.Id,
                agent.Username,
                agent.DisplayName,
                agent.Role,
                true))
            .ToArray();
        if (users.Length == 0)
        {
            throw new InvalidDataException("headcount.xlsx no contiene usuarios activos.");
        }

        return new HeadcountRoster(users, directory);
    }

    private static IReadOnlyList<PaymentRecord> ReadPayments(string path) => WorkbookTable.Read(path).Rows
        .Select(row =>
        {
            var received = row.Date("Fecha_Recepción", "Fecha Recepcion");
            return new PaymentRecord(
                row.Text("Agente"), TextNormalization.AgentId(row.Text("Agente_ID", "Agente ID")), received,
                row.Text("Producto"), row.Text("Concepto_Cálculo", "Concepto Calculo"),
                row.Decimal("Recuperación_por_Gestión", "Recuperacion por Gestion"),
                row.Integer("Semana") ?? (received is null ? null : ISOWeek.GetWeekOfYear(received.Value.ToDateTime(TimeOnly.MinValue))),
                row.Text("Cliente_Unico", "CLIENTE_UNICO"), row.Text("Nombre_Cliente", "Nombre Cliente"),
                row.Text("dia_semana", "Día", "Dia") is { Length: > 0 } day ? day : DayName(received),
                row.Text("Plan_de_Pago", "Plan de Pago"), row.Text("IdPromesa_Asignada", "IdPromesa Asignada"),
                row.Text("Tipo_Asignacion", "Tipo Asignacion"), row.Text("Zona_Geografica", "Zona Geografica"));
        })
        .Where(x => x.Recovery >= 0m)
        .ToArray();

    private static IReadOnlyList<PromiseRecord> ReadPromises(string path) => WorkbookTable.Read(path).Rows
        .Select(row => new PromiseRecord(
            row.Text("IdPromesa", "ID Promesa"), row.Text("Cliente_Unico", "CLIENTE_UNICO"),
            row.Text("Nombre_Cliente", "Nombre Cliente"), row.Text("Numero_Cliente", "Número Cliente"),
            row.Text("Agente"), TextNormalization.AgentId(row.Text("Agente_ID", "Agente ID")),
            row.Date("Fecha_Promesa", "Fecha Promesa"), row.Decimal("Monto_Inicial", "Monto Inicial"),
            row.Decimal("Monto_Semanal", "Monto Semanal"), row.Text("Tipo_Promesa", "Tipo Promesa"),
            row.Integer("Numero_Semanas", "Número de semanas", "Numero Semanas"),
            row.Date("Fecha_Creacion", "Fecha Creacion"), row.Text("Estatus"),
            row.Integer("IdPromesa_Ascendra", "ID Promesa Ascendra", "AscendraPromiseId"),
            SourceCreatedAt: row.Timestamp("Fecha_Creacion", "Fecha Creacion")))
        .ToArray();

    private static IReadOnlyList<PortfolioRecord> ReadPortfolio(string path) => WorkbookTable.Read(path).Rows
        .Select(row => new PortfolioRecord(
            row.Text("CLIENTE_UNICO", "Cliente_Unico"), row.Text("NOMBRE_CTE", "NOMBRE", "Nombre_Cliente", "Nombre Cliente"),
            row.Text("PRODUCTO", "Producto"), OptionalDecimal(row, "SALDO_TOTAL", "Saldo Total", "Saldo"),
            row.Text("ESTATUS", "Estatus"), row.Text("FIDIAPAGO", "Dia de Pago"),
            row.Text("ZONA_GEOGRAFICA", "Zona Geografica"),
            row.TextValuesMatching(header => header.Contains("telefono", StringComparison.Ordinal) || header.Contains("celular", StringComparison.Ordinal))
                .Select(TextNormalization.Phone).Where(phone => phone.Length == 10).Distinct().ToArray()))
        .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
        .ToArray();

    private static IReadOnlyList<ContactRecord> ReadContacts(DataPaths paths, IReadOnlyList<PortfolioRecord> portfolio, ICollection<string> warnings)
    {
        var portfolioByClient = portfolio.ToDictionary(x => x.ClientId, StringComparer.OrdinalIgnoreCase);
        var generated = paths.Generated("CRM_Contactos.xlsx");
        if (File.Exists(generated))
        {
            return WorkbookTable.Read(generated, "Registros").Rows.Select(row =>
            {
                var clientId = row.Text("CLIENTE_UNICO", "Cliente_Unico");
                portfolioByClient.TryGetValue(clientId, out var client);
                var contact = new ContactRecord(
                clientId, TextNormalization.Phone(row.Text("Telefono", "Teléfono", "Telefono 1")),
                row.Text("Referencia", "Nombre_Referencia", "Nombre referencia"), row.Text("Parentesco"),
                row.Text("NOMBRE_CTE", "Nombre_Cliente", "Nombre Cliente", "Nombre"), row.Text("Producto"),
                OptionalDecimal(row, "Saldo_Total", "Saldo total", "Saldo"), row.Text("Estatus"), row.Text("Origen", "Source"));
                return contact with
                {
                    ClientName = string.IsNullOrWhiteSpace(contact.ClientName) ? client?.ClientName ?? string.Empty : contact.ClientName,
                    Product = string.IsNullOrWhiteSpace(contact.Product) ? client?.Product ?? string.Empty : contact.Product,
                    TotalBalance = contact.TotalBalance ?? client?.TotalBalance,
                    Status = string.IsNullOrWhiteSpace(contact.Status) ? client?.Status ?? string.Empty : contact.Status,
                };
            })
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId) && x.Phone.Length == 10).ToArray();
        }

        var raw = paths.Input("Referencias.xlsx");
        if (!File.Exists(raw))
        {
            warnings.Add("Falta CRM_Contactos.xlsx y Referencias.xlsx.");
            return Array.Empty<ContactRecord>();
        }
        return WorkbookTable.Read(raw).Rows.Select(row =>
        {
            var clientId = row.Text("CLIENTE_UNICO", "Cliente_Unico");
            portfolioByClient.TryGetValue(clientId, out var client);
            return new ContactRecord(clientId, TextNormalization.Phone(row.Text("Telefono 1", "Telefono", "Teléfono")),
                row.Text("Referencia"), row.Text("Parentesco"), client?.ClientName ?? string.Empty,
                client?.Product ?? string.Empty, client?.TotalBalance, client?.Status ?? string.Empty, "Referencias");
        }).Where(x => !string.IsNullOrWhiteSpace(x.ClientId) && x.Phone.Length == 10).ToArray();
    }

    private DataPaths Paths()
    {
        var configured = options.CurrentValue;
        return new DataPaths(Resolve(configured.InputPath), Resolve(configured.GeneratedPath));
    }

    private string Resolve(string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);

        var publishedPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
        if (Directory.Exists(publishedPath)) return publishedPath;

        // Durante "dotnet run" el content root es src/Ascendra.Agents.Web;
        // en publicación (Render/Docker) los datos viven directamente en /app/data.
        return Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", path));
    }

    private static string Fingerprint(DataPaths paths, bool postgresDirectory)
    {
        var files = new[]
        {
            paths.Input("agents_collections.xlsx"),
            paths.Input("promesas_estatus.xlsx"), paths.Input("Descarga_cartera.xlsx"), paths.Input("Referencias.xlsx"), paths.Generated("CRM_Contactos.xlsx"),
        };
        if (!postgresDirectory) files = files.Append(paths.Input("headcount.xlsx")).ToArray();
        var material = string.Join('|', files.Select(path => File.Exists(path)
            ? $"{path}:{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}"
            : $"{path}:missing"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..16];
    }

    private static bool IsActive(string value) => TextNormalization.Normalize(value) is "activo" or "active" or "1" or "true" or "si";
    private static bool IsKnownRole(string value) => TextNormalization.Normalize(value) is "agente" or "supervisor" or "gerente" or "director";
    private static string NormaliseRole(string value) => TextNormalization.Normalize(value) switch
    {
        "agente" => "Agente", "supervisor" => "Supervisor", "gerente" => "Gerente", "director" => "Director",
        // Compatibilidad con un registro histórico donde el estado se copió
        // accidentalmente en la columna rol. Sólo concede facultades de agente.
        "activo" => "Agente", _ => "Agente",
    };
    private static bool IsPlaceholder(string value) => TextNormalization.Normalize(value) is "" or "n/a" or "na";
    private static decimal? OptionalDecimal(WorkbookRow row, params string[] candidates)
    {
        var source = row.Text(candidates);
        return string.IsNullOrWhiteSpace(source) ? null : row.Decimal(candidates);
    }
    private static string DayName(DateOnly? date) => date?.DayOfWeek switch
    {
        DayOfWeek.Monday => "Lunes", DayOfWeek.Tuesday => "Martes", DayOfWeek.Wednesday => "Miércoles",
        DayOfWeek.Thursday => "Jueves", DayOfWeek.Friday => "Viernes", DayOfWeek.Saturday => "Sábado",
        DayOfWeek.Sunday => "Domingo", _ => string.Empty,
    };

    private sealed record DataPaths(string InputPath, string GeneratedPath)
    {
        public string Input(string file) => Path.Combine(InputPath, file);
        public string Generated(string file) => Path.Combine(GeneratedPath, file);
    }

    private sealed record HeadcountRoster(
        IReadOnlyList<AgentUser> Users,
        IReadOnlyList<AgentDirectoryRecord> Directory);

    /// <summary>
    /// Resuelve primero por ID y sólo después por un alias único. Así una fila
    /// histórica sin ID puede beneficiarse del padrón, pero dos homónimos nunca
    /// se fusionan por accidente.
    /// </summary>
    private sealed class AgentIdentityCatalog
    {
        private readonly IReadOnlyDictionary<string, AgentDirectoryRecord> _byId;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<AgentDirectoryRecord>> _byAlias;

        public AgentIdentityCatalog(IReadOnlyList<AgentDirectoryRecord> agents)
        {
            _byId = agents.ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
            _byAlias = agents
                .SelectMany(agent => agent.Aliases.Select(alias => new { Alias = TextNormalization.Normalize(alias), Agent = agent }))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Alias))
                .GroupBy(entry => entry.Alias, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<AgentDirectoryRecord>)group
                        .Select(entry => entry.Agent)
                        .DistinctBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        public PaymentRecord Canonicalize(PaymentRecord payment)
        {
            var agent = Resolve(payment.AgentId, payment.Agent);
            return agent is null
                ? payment
                : payment with { Agent = agent.DisplayName, AgentId = agent.Id };
        }

        public PromiseRecord Canonicalize(PromiseRecord promise)
        {
            var agent = Resolve(promise.AgentId, promise.Agent);
            return agent is null
                ? promise
                : promise with { Agent = agent.DisplayName, AgentId = agent.Id };
        }

        private AgentDirectoryRecord? Resolve(string? agentId, string? agentName)
        {
            var normalizedId = TextNormalization.AgentId(agentId);
            if (!string.IsNullOrWhiteSpace(normalizedId) && _byId.TryGetValue(normalizedId, out var byId))
            {
                return byId;
            }

            var normalizedName = TextNormalization.Normalize(agentName);
            return !string.IsNullOrWhiteSpace(normalizedName)
                && _byAlias.TryGetValue(normalizedName, out var candidates)
                && candidates.Count == 1
                ? candidates[0]
                : null;
        }
    }
}
