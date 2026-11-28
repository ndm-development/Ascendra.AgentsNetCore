using System.Globalization;
using System.Text;

namespace Ascendra.Agents.Domain;

public static class IdentityNormalization
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

    public static string AgentId(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim();
        return cleaned.EndsWith(".0", StringComparison.Ordinal)
            && decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? decimal.Truncate(number).ToString(CultureInfo.InvariantCulture)
            : cleaned;
    }

}
