namespace Cockpit.Core.Diagnostics;

// Everything the diagnostics panel reports at one moment (AC-58): platform, rendering, memory, open sessions,
// and crash artifacts. Assembled in the App layer, the only layer that can see the render backend, toolkit
// version and live sessions, so the tester can copy one block of text instead of hunting through OS tools (AC-57).
public sealed record DiagnosticsSnapshot(
    DateTimeOffset CapturedAt,
    PlatformInfo Platform,
    RenderingInfo Rendering,
    ProcessMemoryInfo Memory,
    ManagedHeapInfo ManagedHeap,
    long MachineMemoryBytes,
    IReadOnlyList<SessionDiagnostic> Sessions,
    IReadOnlyList<CrashLogEntry> CrashLogs);

// One open session's contribution (AC-58): named with the resident memory of its whole process tree — the same
// figure the status bar's per-session number uses — since a managed climb can hide in a session's child tree.
// `Kind` is "Agent" or "Terminal"; `ProcessId` is null for an HTTP-backed provider with nothing local to weigh.
public sealed record SessionDiagnostic(string Title, string Kind, int? ProcessId, long ResidentBytes);
