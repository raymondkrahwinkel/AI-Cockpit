namespace Cockpit.Core.Debugging;

// Whether the cockpit shows its diagnostic controls (#73), persisted under the `debug` section of
// `cockpit.json` (same store pattern as the layout and session-behaviour settings). These are the controls
// that exist to investigate the cockpit itself rather than to do the work — the TTY's Redraw button, say. They
// are off by default: a header strip is small, and a button most operators never need does not belong in it.
public sealed record DebugSettings
{
    // When true, diagnostic controls appear (the TTY session header's Redraw). Off by default.
    public bool ShowDebugControls { get; init; }
}
