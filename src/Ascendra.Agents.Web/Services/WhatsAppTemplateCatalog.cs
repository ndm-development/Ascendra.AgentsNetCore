using System.Text.Json;
using System.Text.RegularExpressions;
using Ascendra.Agents.Web.Domain;
using Ascendra.Agents.Web.Infrastructure;

namespace Ascendra.Agents.Web.Services;

public sealed partial class WhatsAppTemplateCatalog(IHostEnvironment environment, ILogger<WhatsAppTemplateCatalog> logger)
{
    private IReadOnlyList<WhatsAppTemplate>? _templates;

    public IReadOnlyList<WhatsAppTemplate> All => _templates ??= Load();

    public string Build(string templateValue, ContactRecord client, string agentShortName, DateTime? now = null)
    {
        var template = All.FirstOrDefault(x => x.Value == templateValue)?.Message;
        if (string.IsNullOrWhiteSpace(template)) return "Selecciona una nomenclatura para preparar el mensaje.";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nombre"] = client.ClientName,
            ["cuenta"] = client.ClientId,
            ["clienteunico"] = client.ClientId,
            ["producto"] = client.Product,
            ["monto"] = client.TotalBalance is null ? string.Empty : DashboardQueries.Currency(client.TotalBalance.Value),
            ["montototal"] = client.TotalBalance is null ? string.Empty : DashboardQueries.Currency(client.TotalBalance.Value),
            ["saldototal"] = client.TotalBalance is null ? string.Empty : DashboardQueries.Currency(client.TotalBalance.Value),
            ["referencia"] = client.ReferenceName,
            ["telefono"] = client.Phone,
        };
        var personalized = VariablePattern().Replace(template, match =>
        {
            var key = TextNormalization.Header(match.Groups[1].Value);
            return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? $"*{value}*" : match.Value;
        });
        if (!string.IsNullOrWhiteSpace(agentShortName))
        {
            personalized = AgentPattern().Replace(personalized, agentShortName);
        }
        return GreetingPattern().Replace(personalized, Greeting(now));
    }

    private IReadOnlyList<WhatsAppTemplate> Load()
    {
        var relative = Path.Combine("data", "config", "whatsapp_templates.json");
        var publishedPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, relative));
        var path = File.Exists(publishedPath)
            ? publishedPath
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", relative));
        try
        {
            var templates = JsonSerializer.Deserialize<List<WhatsAppTemplate>>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? [];
            return templates.Count > 0 && templates.All(template => !string.IsNullOrWhiteSpace(template.Value))
                ? templates
                : throw new InvalidDataException("No hay nomenclaturas válidas.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            logger.LogError(exception, "No se pudo cargar el catálogo de WhatsApp.");
            return [];
        }
    }

    private static string Greeting(DateTime? now) => (now ?? DateTime.Now).Hour switch
    {
        < 12 => "Buenos días", < 20 => "Buenas tardes", _ => "Buenas noches",
    };

    [GeneratedRegex(@"\*([^*\n]+)\*", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
    [GeneratedRegex(@"\bAndrea\s+Jim[eé]nez\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgentPattern();
    [GeneratedRegex(@"\b(?:buenos\s+d[ií]as|buena(?:s)?\s+tarde(?:s)?|buenas\s+noches)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GreetingPattern();
}
