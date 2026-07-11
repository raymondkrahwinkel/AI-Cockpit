namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Base type for every typed event an <see cref="IPluginSessionDriver"/> can raise (#45) — the plugin-facing
/// mirror of <c>Cockpit.Core.Claude.ClaudeSessionEvent</c>, trimmed to the subset a third-party HTTP provider
/// can actually produce (no Claude-CLI-only thinking/status/rate-limit events). The host's driver adapter
/// maps each subtype to its <c>ClaudeSessionEvent</c> counterpart so the rest of the app sees one event
/// vocabulary regardless of which driver produced it.
/// </summary>
public abstract record PluginSessionEvent
{
    /// <summary>Session id the driver assigned, once known.</summary>
    public required string? SessionId { get; init; }
}
