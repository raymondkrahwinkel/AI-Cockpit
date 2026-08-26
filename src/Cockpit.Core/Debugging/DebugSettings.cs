namespace Cockpit.Core.Debugging;

// Diagnostic controls are persisted under `debug` in `cockpit.json` (#73), following the normal settings store pattern.
// They are off by default: controls for investigating Cockpit itself do not merit scarce header space for most operators.
public sealed record DebugSettings
{
    // When true, diagnostic controls appear (the TTY session header's Redraw). Off by default.
    public bool ShowDebugControls { get; init; }

    // AC-718: when true, a background service writes one diagnostics line to the log every few seconds
    // (memory, GC, handles, threads) — off by default for the overhead and log growth a healthy run does not
    // need. The UI-thread freeze heartbeat is separate and always on; it costs nothing until a hang happens.
    public bool LogDiagnosticSnapshots { get; init; }
}
