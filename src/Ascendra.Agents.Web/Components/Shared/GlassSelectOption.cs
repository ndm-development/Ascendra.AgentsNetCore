namespace Ascendra.Agents.Web.Components.Shared;

/// <summary>
/// An option rendered by <see cref="GlassSelect"/>. <paramref name="Group"/>
/// keeps related outcomes together without relying on a browser-native select menu.
/// </summary>
public sealed record GlassSelectOption(
    string Value,
    string Label,
    string? Group = null,
    string? SelectedLabel = null,
    bool Disabled = false);
