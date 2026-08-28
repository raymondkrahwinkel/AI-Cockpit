namespace Cockpit.Core.Debugging;

// Diagnostic controls are persisted under `debug` in `cockpit.json` (#73), following the normal settings store pattern.
// They are off by default: controls for investigating Cockpit itself do not merit scarce header space for most operators.
public sealed record DebugSettings
{
    // When true, diagnostic controls appear (the TTY session header's Redraw). Off by default.
    public bool ShowDebugControls { get; init; }

    // AC-1125: on by default. A freeze reported from a machine nobody has toggled this on for has no heap/rss
    // history to read — measured cost ~2 MB/day on a 10s cadence (~8,600 lines/day), and the log already rotates.
    // The UI-thread freeze heartbeat is separate and always on regardless of this flag.
    public bool LogDiagnosticSnapshots { get; init; } = true;
}
