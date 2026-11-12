using System.Globalization;
using System.Text;

namespace Ascendra.Agents.Web.Infrastructure;

public static class TextNormalization
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }
        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC)
            .Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static string Header(string? value) => new(Normalize(value).Where(char.IsLetterOrDigit).ToArray());

    public static string Phone(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    public static string AgentId(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim();
        return cleaned.EndsWith(".0", StringComparison.Ordinal)
            && decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? decimal.Truncate(number).ToString(CultureInfo.InvariantCulture)
            : cleaned;
    }

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
