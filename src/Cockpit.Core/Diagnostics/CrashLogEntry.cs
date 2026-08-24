namespace Cockpit.Core.Diagnostics;

// One crash or memory-kill artifact the OS wrote, surfaced with a path and summary so the tester never has to
// know where the OS hides it (AC-58; AC-57 stalled for lack of this). `Path` is empty when the entry is a log
// line rather than a file (an OOM-killer message); `Timestamp` is null when unknown.
public sealed record CrashLogEntry(string Source, string Path, DateTimeOffset? Timestamp, string Summary);
