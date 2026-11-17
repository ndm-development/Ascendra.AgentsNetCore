namespace Ascendra.Agents.Web.Infrastructure;

/// <summary>
/// Resuelve la carpeta operativa de imágenes y añade una versión derivada de
/// cada archivo. Así, al sustituir un fondo en <c>/assets</c>, el navegador no
/// conserva la imagen anterior en caché después del despliegue.
/// </summary>
public sealed class OperationalAssetUrls(IWebHostEnvironment environment)
{
    private readonly string? _directory = ResolvePath(environment);
    private readonly string _webRootPath = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");

    public string Url(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeName, StringComparison.Ordinal))
        {
            throw new ArgumentException("El nombre del recurso no es válido.", nameof(fileName));
        }

        var path = _directory is null ? null : Path.Combine(_directory, safeName);
        var version = path is not null && File.Exists(path)
            ? File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";

        return $"/assets/{Uri.EscapeDataString(safeName)}?v={version}";
    }

    /// <summary>
    /// Entrega CSS y JavaScript propios con una versión de archivo. Una nueva
    /// publicación no depende de que el navegador decida invalidar la misma URL.
    /// </summary>
    public string WebUrl(string relativePath)
    {
        var safePath = relativePath.Replace('\\', '/').TrimStart('/');
        if (safePath is not ("css/app.css" or "js/charts.js" or "js/session.js"))
        {
            throw new ArgumentException("El recurso web no es válido.", nameof(relativePath));
        }

        var path = Path.Combine(_webRootPath, safePath.Replace('/', Path.DirectorySeparatorChar));
        var version = File.Exists(path)
            ? File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";

        return $"/{safePath}?v={version}";
    }

    public static string? ResolvePath(IWebHostEnvironment environment)
    {
        var developmentPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "assets"));
        if (Directory.Exists(developmentPath)) return developmentPath;

        var publishedPath = Path.Combine(environment.ContentRootPath, "assets");
        return Directory.Exists(publishedPath) ? publishedPath : null;
    }
}
