namespace Ascendra.Agents.Web.Infrastructure;

public sealed class AgentDataOptions
{
    public string InputPath { get; init; } = "data/input";
    public string GeneratedPath { get; init; } = "data/generated";
    public int RefreshSeconds { get; init; } = 45;
}
