namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>Session-level metadata reported once at the start of a plugin-driven session's stream.</summary>
public sealed record PluginSessionInitialized : PluginSessionEvent
{
    public required IReadOnlyList<string> Tools { get; init; }
}
