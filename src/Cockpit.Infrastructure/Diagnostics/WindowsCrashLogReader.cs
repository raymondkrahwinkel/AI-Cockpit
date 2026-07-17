using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

/// <summary>
/// Windows crash dumps, from the Windows Error Reporting folder <c>%LOCALAPPDATA%\CrashDumps</c> (AC-58). WER
/// writes a <c>.dmp</c> there per crashing process named for the executable, so filtering to the cockpit's own is
/// a filename match — the cross-platform rounding-out of the macOS case AC-57 was about.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCrashLogReader : ICrashLogReader
{
    public IReadOnlyList<CrashLogEntry> RecentEntries(int max)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrashDumps");

        return CrashLogFiles.Newest(
            directory,
            name => CrashLogFiles.NamesCockpit(name) && name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase),
            "Windows crash dump",
            max);
    }
}
