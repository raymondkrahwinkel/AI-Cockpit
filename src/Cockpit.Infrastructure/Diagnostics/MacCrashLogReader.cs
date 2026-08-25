using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

// macOS crash and memory-kill reports, from `~/Library/Logs/DiagnosticReports` (AC-58). Crashes land as
// `.ips`/`.crash`; a memory-pressure kill leaves a `JetsamEvent-*.ips`, shown even without naming the app
// because its absence was the clue AC-57's crash was in-process, not a jetsam kill.
[SupportedOSPlatform("macos")]
internal sealed class MacCrashLogReader : ICrashLogReader
{
    public IReadOnlyList<CrashLogEntry> RecentEntries(int max)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "DiagnosticReports");

        var appReports = CrashLogFiles.Newest(
            directory,
            name => CrashLogFiles.NamesCockpit(name) && _IsCrashReport(name),
            "macOS crash report",
            max);

        var jetsam = CrashLogFiles.Newest(
            directory,
            name => name.StartsWith("JetsamEvent", StringComparison.OrdinalIgnoreCase),
            "macOS memory-pressure kill",
            max);

        return appReports
            .Concat(jetsam)
            .OrderByDescending(entry => entry.Timestamp)
            .Take(max)
            .ToList();
    }

    private static bool _IsCrashReport(string name) =>
        name.EndsWith(".ips", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".crash", StringComparison.OrdinalIgnoreCase);
}
