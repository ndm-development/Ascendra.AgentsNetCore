using System.Globalization;
using System.Text;

namespace Ascendra.Agents.Infrastructure;

public static class TextNormalization
{
    public static string Normalize(string? value) => IdentityNormalization.Normalize(value);

    public static string Header(string? value) => new(Normalize(value).Where(char.IsLetterOrDigit).ToArray());

    public static string Phone(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    public static string AgentId(string? value) => IdentityNormalization.AgentId(value);

    public static bool IsOrganic(string? agent) => Normalize(agent).Contains("organico", StringComparison.Ordinal);

    public static bool AgentNameMatches(string sourceName, string visibleName)
    {
        var source = Normalize(sourceName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var visible = Normalize(visibleName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (source.Length < 2 || visible.Length < 2) return false;
        if (visible.All(token => source.Contains(token, StringComparer.Ordinal))) return true;
        return source.Length == 3 && visible.Length == 3 && source.SequenceEqual(visible);
    }
}
