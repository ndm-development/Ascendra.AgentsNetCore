using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Web.Auth;

public sealed class DirectoryAuthenticationStateProvider(
    ILoggerFactory loggerFactory, AgentAuthenticator authenticator,
    IOptions<Infrastructure.AgentDirectoryOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DirectoryAuthenticationStateProvider>();
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(15);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        if (!options.Value.UsePostgres) return true;
        try { return await authenticator.IsSessionCurrentAsync(authenticationState.User, cancellationToken); }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
        {
            _logger.LogWarning("No se pudo revalidar el padrón de la sesión abierta.");
            return false;
        }
    }
}
