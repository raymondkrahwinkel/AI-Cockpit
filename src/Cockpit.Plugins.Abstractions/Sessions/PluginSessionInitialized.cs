namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>Session-level metadata reported once at the start of a plugin-driven session's stream.</summary>
public sealed record PluginSessionInitialized : PluginSessionEvent
{
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>
    /// The working directory the session runs in (#45 D3), so host features that follow the active session's
    /// directory (the git-status header, the active-cwd observer) work for a plugin session too. Optional — a
    /// provider with no directory of its own (an HTTP model) leaves it <see langword="null"/>, and an
    /// already-compiled plugin that never sets it keeps constructing this the old way.
    /// </summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// The model the session actually started under, when the provider's own init event names it (AC-141) — a
    /// session launched with no explicit model (Auto/default) otherwise leaves the host's model live-control with
    /// nothing to show. Optional, and only ever used to seed a control's starting value, never to fire a live
    /// switch back at the driver: a provider with no such concept, or an already-compiled plugin that never sets
    /// it, keeps constructing this the old way.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Capability names the CLI advertised on this same init line (AC-739), e.g. <c>interrupt_cancel_queued_v1</c>.
    /// Empty for a CLI too old to send the field, and for an already-compiled plugin that never sets it.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}
