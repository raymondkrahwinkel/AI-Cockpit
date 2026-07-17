namespace Cockpit.Core.Diagnostics;

/// <summary>
/// Everything the diagnostics panel reports at one moment (AC-58): what the cockpit runs on, how it draws, what it
/// is holding in native and managed memory, the sessions open, and the crash artifacts the OS left behind. It is
/// assembled in the App layer (the only one that can see the render backend, the toolkit version and the live
/// sessions) and exists so the tester can copy one block of text instead of hunting through Activity Monitor and
/// crash-report folders — the exact gap that made AC-57 hard to diagnose.
/// </summary>
public sealed record DiagnosticsSnapshot(
    DateTimeOffset CapturedAt,
    PlatformInfo Platform,
    RenderingInfo Rendering,
    ProcessMemoryInfo Memory,
    ManagedHeapInfo ManagedHeap,
    long MachineMemoryBytes,
    IReadOnlyList<SessionDiagnostic> Sessions,
    IReadOnlyList<CrashLogEntry> CrashLogs);

/// <summary>One open session's contribution (AC-58): a managed climb can hide in a session's child tree, so each is
/// named with the resident memory of its whole process tree — the same figure the status bar's per-session number uses.</summary>
/// <param name="Kind">What kind of session it is — "Agent" (a CLI-backed provider) or "Terminal" — so a leak that only shows with one kind open is visible.</param>
/// <param name="ProcessId">The session's own process id, or null for an HTTP-backed provider with nothing local to weigh.</param>
public sealed record SessionDiagnostic(string Title, string Kind, int? ProcessId, long ResidentBytes);
