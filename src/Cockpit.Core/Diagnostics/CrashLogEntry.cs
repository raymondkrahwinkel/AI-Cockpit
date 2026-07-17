namespace Cockpit.Core.Diagnostics;

/// <summary>
/// One crash or memory-kill artifact the OS wrote, surfaced so the tester never has to know where the OS hides it
/// (AC-58). AC-57 stalled precisely on this: no jetsam file, and the crash-report location unknown to the tester.
/// The panel lists the newest few with a path they can open and text they can paste.
/// </summary>
/// <param name="Source">Human-readable origin — "macOS crash report", "Linux OOM (journal)", "Windows crash dump".</param>
/// <param name="Path">Full path to the artifact, so the tester can open or attach it. Empty when the entry is a log line rather than a file (an OOM-killer message).</param>
/// <param name="Timestamp">When it happened, when known — from the file's write time or the log line's own timestamp.</param>
/// <param name="Summary">A short reason or first line: the crashing module, the termination reason, or the OOM message.</param>
public sealed record CrashLogEntry(string Source, string Path, DateTimeOffset? Timestamp, string Summary);
