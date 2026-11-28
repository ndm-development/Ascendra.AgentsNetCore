using System.Security.Cryptography;

namespace Ascendra.Agents.Infrastructure;

public sealed record HeadcountImport(IReadOnlyList<AgentDirectoryRecord> Directory,
    string Fingerprint, IReadOnlyList<string> Warnings);

/// <summary>Adaptador de carga inicial; el archivo deja de consultarse después de importar el padrón.</summary>
public sealed class HeadcountImportReader
{
    public HeadcountImport Read(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException("Falta headcount.xlsx para inicializar el padrón de PostgreSQL.");
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);
        var workbook = WorkbookTable.Read(stream, fileName: Path.GetFileName(path));
        var required = new[] { "id", "nombre", "a_paterno", "a_materno", "nombre_visible", "usuario", "estado", "rol" };
        var missing = required.Where(header => !workbook.HasHeader(header)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"headcount.xlsx no contiene: {string.Join(", ", missing)}.");
        var warnings = new List<string>();
        var directory = new List<AgentDirectoryRecord>();
        foreach (var row in workbook.Rows)
        {
            if (required.All(header => string.IsNullOrWhiteSpace(row.Text(header)))) continue;
            var active = IdentityNormalization.Normalize(row.Text("estado")) is "activo" or "active" or "1" or "true" or "si";
            var rawRole = IdentityNormalization.Normalize(row.Text("rol"));
            var role = AgentDirectoryRules.Roles.FirstOrDefault(value => IdentityNormalization.Normalize(value) == rawRole);
            if (role is null)
            {
                if (active && rawRole != "activo")
                    throw new InvalidDataException("headcount.xlsx contiene un rol activo no reconocido.");
                role = "Agente";
                warnings.Add("Se conservó como Agente un rol heredado no reconocido.");
            }
            directory.Add(new AgentDirectoryRecord(row.Text("id"), row.Text("nombre"), row.Text("a_paterno"),
                row.Text("a_materno"), row.Text("nombre_visible"), row.Text("usuario"), role, active));
        }
        var validated = AgentDirectoryRules.Validate(directory, requireActiveUsers: true);
        return new HeadcountImport(validated, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), warnings.AsReadOnly());
    }
}
