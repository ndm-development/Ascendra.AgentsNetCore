using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Ascendra.Agents.Web.Domain;
using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Web.Infrastructure;

/// <summary>
/// Lee cortes completos de Excel en memoria. Cada solicitud consume un único
/// snapshot inmutable: nunca mezcla archivos de dos cortes distintos.
/// </summary>
public sealed class ExcelSnapshotStore(
    IOptionsMonitor<AgentDataOptions> options,
    IHostEnvironment environment,
    ILogger<ExcelSnapshotStore> logger)
{
    private readonly object _refreshLock = new();
    private AgentDataSnapshot _current = AgentDataSnapshot.Empty;
    private DataHealth _health = new(false, "Deposita los Excel de entrada para iniciar el dashboard.", null, "pending");
    private string? _lastFingerprint;

    public AgentDataSnapshot Current => Volatile.Read(ref _current);
    public DataHealth Health => Volatile.Read(ref _health);
    public int RefreshSeconds => Math.Clamp(options.CurrentValue.RefreshSeconds, 15, 900);

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_refreshLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = Paths();
            var fingerprint = Fingerprint(paths);
            if (fingerprint == _lastFingerprint) return Task.CompletedTask;

            try
            {
                var snapshot = Load(paths, fingerprint);
                Volatile.Write(ref _current, snapshot);
                Volatile.Write(ref _health, new DataHealth(
                    snapshot.Users.Count > 0,
                    snapshot.Users.Count > 0
                        ? $"Corte cargado · {snapshot.LoadedAt:dd/MM/yyyy HH:mm}"
                        : "Falta usuarios_ascendra.xlsx para habilitar el acceso.",
                    snapshot.LoadedAt,
                    snapshot.Version));
                _lastFingerprint = fingerprint;
                logger.LogInformation("Corte de Agentes actualizado. Pagos: {Payments}; promesas: {Promises}; contactos: {Contacts}.",
                    snapshot.Payments.Count, snapshot.Promises.Count, snapshot.Contacts.Count);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                var current = Current;
                var message = current == AgentDataSnapshot.Empty
                    ? "No se pudo leer el corte. Revisa los nombres y columnas de los Excel."
                    : "El nuevo corte tiene un problema; se conserva el último corte válido.";
                Volatile.Write(ref _health, new DataHealth(current.Users.Count > 0, message, current.LoadedAt == DateTimeOffset.MinValue ? null : current.LoadedAt, current.Version));
                logger.LogError(exception, "No se pudo actualizar el corte de Agentes.");
            }
        }
        return Task.CompletedTask;
    }

    private AgentDataSnapshot Load(DataPaths paths, string fingerprint)
    {
        var warnings = new List<string>();
        var users = ReadOptional(paths.Input("usuarios_ascendra.xlsx"), ReadUsers, warnings);
        var directory = ReadOptional(paths.Input("agentes_activos.xlsx"), ReadAgentDirectory, warnings);
        var payments = ReadOptional(paths.Input("agents_collections.xlsx"), ReadPayments, warnings)
            .Where(x => !TextNormalization.IsOrganic(x.Agent)).ToArray();
        var promises = ReadOptional(paths.Input("promesas_estatus.xlsx"), ReadPromises, warnings)
            .Where(x => !TextNormalization.IsOrganic(x.Agent)).ToArray();
        var portfolio = ReadOptional(paths.Input("Descarga_cartera.xlsx"), ReadPortfolio, warnings);
        var contacts = ReadContacts(paths, portfolio, warnings);
        return new AgentDataSnapshot(
            fingerprint,
            DateTimeOffset.Now,
            users,
            payments,
            promises,
            portfolio,
            contacts,
            directory,
            warnings);
    }

    private static IReadOnlyList<T> ReadOptional<T>(string path, Func<string, IReadOnlyList<T>> read, ICollection<string> warnings)
    {
        if (!File.Exists(path))
        {
            warnings.Add($"Falta {Path.GetFileName(path)}.");
            return Array.Empty<T>();
        }
        return read(path);
    }

    private static IReadOnlyList<AgentUser> ReadUsers(string path) => WorkbookTable.Read(path).Rows
        .Select(row => new AgentUser(
            TextNormalization.AgentId(row.Text("id")),
            TextNormalization.Normalize(row.Text("usuario")),
            row.Text("nombre_visible"),
            NormaliseRole(row.Text("rol")),
            IsActive(row.Text("estado"))))
        .Where(x => !string.IsNullOrWhiteSpace(x.Username) && !string.IsNullOrWhiteSpace(x.DisplayName))
        .ToArray();

    private static IReadOnlyList<AgentDirectoryRecord> ReadAgentDirectory(string path) => WorkbookTable.Read(path).Rows
        .Select(row => new AgentDirectoryRecord(
            TextNormalization.AgentId(row.Text("ID")), row.Text("NOMBRE"), row.Text("A. PATERNO", "APATERNO")))
        .Where(x => !string.IsNullOrWhiteSpace(x.Id))
        .ToArray();

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
            row.Date("Fecha_Creacion", "Fecha Creacion"), row.Text("Estatus")))
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

    private static string Fingerprint(DataPaths paths)
    {
        var files = new[]
        {
            paths.Input("usuarios_ascendra.xlsx"), paths.Input("agentes_activos.xlsx"), paths.Input("agents_collections.xlsx"),
            paths.Input("promesas_estatus.xlsx"), paths.Input("Descarga_cartera.xlsx"), paths.Input("Referencias.xlsx"), paths.Generated("CRM_Contactos.xlsx"),
        };
        var material = string.Join('|', files.Select(path => File.Exists(path)
            ? $"{path}:{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}"
            : $"{path}:missing"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..16];
    }

    private static bool IsActive(string value) => TextNormalization.Normalize(value) is "activo" or "active" or "1" or "true" or "si";
    private static string NormaliseRole(string value) => TextNormalization.Normalize(value) switch
    {
        "agente" => "Agente", "supervisor" => "Supervisor", "gerente" => "Gerente", _ => "Agente",
    };
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
}
