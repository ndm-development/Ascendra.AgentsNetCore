namespace Ascendra.Agents.Web.Auth;

public sealed class SecurityOptions
{
    public string CredentialsEnvironmentVariable { get; init; } = "ASCENDRA_AGENT_CREDENTIALS_JSON";
    public bool AllowLocalPreview { get; init; }
    public string DataProtectionPath { get; init; } = string.Empty;
}
