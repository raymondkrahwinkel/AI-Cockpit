using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

/// <summary>
/// macOS crash and memory-kill reports, from <c>~/Library/Logs/DiagnosticReports</c> (AC-58). This is the folder
/// AC-57 needed and the tester could not find: app crashes land here as <c>.ips</c> (and legacy <c>.crash</c>)
/// files, and a real memory-pressure kill leaves a <c>JetsamEvent-*.ips</c> — which is shown even though it does
/// not name the app, because its absence was itself the clue that AC-57's crash was in-process, not a jetsam kill.
/// </summary>
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
