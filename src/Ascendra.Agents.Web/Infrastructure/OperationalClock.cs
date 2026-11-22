using System.Globalization;

namespace Ascendra.Agents.Web.Infrastructure;

/// <summary>
/// Presenta instantes operativos en la zona horaria de la operación, sin
/// depender de la zona configurada en el contenedor de Render (UTC).
/// </summary>
public static class OperationalClock
{
    private static readonly CultureInfo MexicoCulture = CultureInfo.GetCultureInfo("es-MX");
    private static readonly TimeZoneInfo MexicoCityTimeZone = ResolveMexicoCityTimeZone();

    public static string Format(DateTimeOffset instant, string format) =>
        TimeZoneInfo.ConvertTime(instant, MexicoCityTimeZone).ToString(format, MexicoCulture);

    public static DateOnly Date(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, MexicoCityTimeZone).DateTime);

    public static DateTime LocalDateTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, MexicoCityTimeZone).DateTime;

    /// <summary>Interpreta una fecha sin zona de las fuentes operativas como hora de Ciudad de México.</summary>
    public static DateTimeOffset FromMexicoCityLocal(DateTime value)
    {
        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, MexicoCityTimeZone.GetUtcOffset(local));
    }

    private static TimeZoneInfo ResolveMexicoCityTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
