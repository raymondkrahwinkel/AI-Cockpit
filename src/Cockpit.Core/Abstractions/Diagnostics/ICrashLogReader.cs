using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Abstractions.Diagnostics;

/// <summary>
/// Discovers the OS's own crash and memory-kill artifacts for the current platform (AC-58), so the diagnostics
/// panel can show them without the tester knowing where the OS keeps them. Implemented per platform — macOS
/// DiagnosticReports, the Linux journal/core dumps, Windows WER — because every OS hides them somewhere different.
/// </summary>
public interface ICrashLogReader
{
    /// <summary>
    /// The newest few relevant artifacts, newest first, capped at <paramref name="max"/>. Reads only; returns an
    /// empty list — never throws — when there are none, or when a tool it needs (coredumpctl, journalctl) is
    /// missing or forbidden. "None found" is a normal, healthy answer.
    /// </summary>
    IReadOnlyList<CrashLogEntry> RecentEntries(int max);
}
