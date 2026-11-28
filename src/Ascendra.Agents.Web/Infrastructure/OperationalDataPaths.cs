using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Web.Infrastructure;

public sealed class OperationalDataPaths(IOptionsMonitor<AgentDataOptions> options, IHostEnvironment environment)
{
    public string Input(string file) => Path.Combine(Resolve(options.CurrentValue.InputPath), file);
    public string Generated(string file) => Path.Combine(Resolve(options.CurrentValue.GeneratedPath), file);

    private string Resolve(string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        var published = Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
        return Directory.Exists(published) ? published
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", path));
    }
}
