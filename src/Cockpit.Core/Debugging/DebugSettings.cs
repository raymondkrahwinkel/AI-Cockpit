namespace Cockpit.Core.Debugging;

/// <summary>
/// Whether the cockpit shows its diagnostic controls (#73), persisted under the <c>debug</c> section of
/// <c>cockpit.json</c> (same store pattern as the layout and session-behaviour settings). These are the controls
/// that exist to investigate the cockpit itself rather than to do the work — the TTY's Redraw button, say. They
/// are off by default: a header strip is small, and a button most operators never need does not belong in it.
/// </summary>
public sealed record DebugSettings
{
    /// <summary>When true, diagnostic controls appear (the TTY session header's Redraw). Off by default.</summary>
    public bool ShowDebugControls { get; init; }
}
