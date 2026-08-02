using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

// Shared file-scan for the crash-report readers that read a directory (macOS DiagnosticReports, Windows WER):
// the newest matching files, turned into entries. Linux is the odd one out — it queries the journal and
// coredumpctl instead of a folder — so it does not use this.
internal static class CrashLogFiles
{
    // The newest files in `directory` whose name matches, newest first, capped at
    // `max`. A missing directory is not an error — it means the OS has written no crash reports,
    // which is the healthy case — so it yields an empty list rather than throwing.
    public static IReadOnlyList<CrashLogEntry> Newest(
        string directory, Func<string, bool> nameMatches, string source, int max)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles()
                .Where(file => nameMatches(file.Name))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(max)
                .Select(file => new CrashLogEntry(
                    source,
                    file.FullName,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                    file.Name))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Whether a crash-artifact filename belongs to the cockpit. Loose on purpose: the file is named for the
    // executable or bundle, which is "Cockpit.App" on a plain build and "AI-Cockpit" in a packaged one.
    public static bool NamesCockpit(string fileName) =>
        fileName.Contains("Cockpit", StringComparison.OrdinalIgnoreCase);
}
