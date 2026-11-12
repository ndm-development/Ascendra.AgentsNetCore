using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Ascendra.Agents.Web.Domain;
using Ascendra.Agents.Web.Infrastructure;
using Norgerman.Cryptography.Scrypt;
using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Web.Auth;

/// <summary>
/// Une el padrón de usuarios en Excel con el secreto de credenciales. El Excel
/// nunca contiene contraseñas y el secreto jamás se devuelve al navegador.
/// </summary>
public sealed class AgentAuthenticator(
    ExcelSnapshotStore snapshots,
    IOptionsMonitor<SecurityOptions> options,
    IHostEnvironment environment,
    ILogger<AgentAuthenticator> logger)
{
    public string RegistryVersion => snapshots.Current.Version;

    public bool IsSessionCurrent(ClaimsPrincipal? principal)
    {
        var username = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var version = principal?.FindFirstValue(AgentClaimTypes.RegistryVersion);
        if (string.IsNullOrWhiteSpace(username) || !string.Equals(version, RegistryVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (CanUseLocalPreview() && username == "preview") return true;
        return snapshots.Current.Users.Any(user => user.IsActive && string.Equals(user.Username, username, StringComparison.Ordinal));
    }

    public AgentUser? Authenticate(string username, string password)
    {
        var normalized = TextNormalization.Normalize(username);
        var user = snapshots.Current.Users.FirstOrDefault(x =>
            x.IsActive && string.Equals(x.Username, normalized, StringComparison.Ordinal));

        if (user is null && CanUseLocalPreview() && normalized == "preview" && password == "preview")
        {
            // La cuenta de vista previa debe reflejar el circuito real de Agentes:
            // Referencias, WhatsApp, Mi recuperación y Mis promesas.
            return new AgentUser("preview", "preview", "Vista local", "Agente", true);
        }

        if (user is null || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var raw = Environment.GetEnvironmentVariable(options.CurrentValue.CredentialsEnvironmentVariable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            logger.LogWarning("No hay credenciales configuradas para el padrón activo.");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty(user.Username, out var entry))
            {
                // El secreto puede conservar mayúsculas o acentos en sus claves históricas.
                entry = document.RootElement.EnumerateObject()
                    .FirstOrDefault(x => TextNormalization.Normalize(x.Name) == user.Username).Value;
            }
            return entry.ValueKind != JsonValueKind.Undefined && CredentialVerifier.Verify(entry, password)
                ? user
                : null;
        }
        catch (JsonException)
        {
            logger.LogError("El secreto de credenciales no contiene JSON válido.");
            return null;
        }
    }

    private bool CanUseLocalPreview() => environment.IsDevelopment() && options.CurrentValue.AllowLocalPreview;
}

internal static class CredentialVerifier
{
    public static bool Verify(JsonElement entry, string candidate)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            return FixedTimeEquals(entry.GetString(), candidate);
        }
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (entry.TryGetProperty("password", out var password))
        {
            return FixedTimeEquals(password.GetString(), candidate);
        }
        if (!entry.TryGetProperty("password_hash", out var hash))
        {
            return false;
        }
        return VerifyWerkzeugHash(hash.GetString(), candidate);
    }

    private static bool VerifyWerkzeugHash(string? stored, string candidate)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var pieces = stored.Split('$');
        if (pieces.Length != 3) return false;
        var method = pieces[0].Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (method.Length == 0) return false;
        if (!TryHex(pieces[2], out var expected)) return false;
        var salt = Encoding.UTF8.GetBytes(pieces[1]);

        byte[] actual;
        if (method[0].Equals("scrypt", StringComparison.OrdinalIgnoreCase) && method.Length == 4
            && int.TryParse(method[1], out var n) && int.TryParse(method[2], out var r)
            && int.TryParse(method[3], out var p))
        {
            actual = ScryptUtil.Scrypt(candidate, salt, n, r, p, expected.Length);
        }
        else if (method[0].Equals("pbkdf2", StringComparison.OrdinalIgnoreCase) && method.Length >= 3
            && int.TryParse(method[^1], out var iterations))
        {
            var algorithm = method[1].Equals("sha512", StringComparison.OrdinalIgnoreCase)
                ? HashAlgorithmName.SHA512
                : HashAlgorithmName.SHA256;
            actual = Rfc2898DeriveBytes.Pbkdf2(candidate, salt, iterations, algorithm, expected.Length);
        }
        else
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool FixedTimeEquals(string? left, string right)
    {
        if (left is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }

    private static bool TryHex(string value, out byte[] bytes)
    {
        try { bytes = Convert.FromHexString(value); return true; }
        catch (FormatException) { bytes = Array.Empty<byte>(); return false; }
    }
}
