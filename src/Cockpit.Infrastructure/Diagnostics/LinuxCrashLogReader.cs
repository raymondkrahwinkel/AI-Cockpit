using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

/// <summary>
/// Linux crash and out-of-memory artifacts (AC-58). Linux keeps them in two places, neither a folder: core dumps
/// live behind <c>coredumpctl</c>, and the OOM killer writes to the kernel log read with <c>journalctl -k</c>. Both
/// are best-effort — the tools may be absent, and reading the kernel log can need a group the user is not in — so a
/// missing tool or a permission wall yields "nothing found", never an error.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxCrashLogReader : ICrashLogReader
{
    private static readonly string[] OomMarkers = ["out of memory", "killed process", "oom-kill", "oom_reaper"];

    public IReadOnlyList<CrashLogEntry> RecentEntries(int max)
    {
        var entries = new List<CrashLogEntry>();
        entries.AddRange(_CoreDumps(max));
        entries.AddRange(_OutOfMemoryKills(max));

        return entries
            .OrderByDescending(entry => entry.Timestamp)
            .Take(max)
            .ToList();
    }

    private static IEnumerable<CrashLogEntry> _CoreDumps(int max)
    {
        // COMM match on the cockpit binary; --reverse puts the newest first, --no-legend drops the header row.
        var lines = _Run("coredumpctl", "--no-legend", "--reverse", "list", "Cockpit.App");

        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(max)
            .Select(line =>
            {
                var collapsed = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return new CrashLogEntry("Linux core dump", string.Empty, _CoredumpTime(collapsed), collapsed);
            })
            .ToList();
    }

    private static IEnumerable<CrashLogEntry> _OutOfMemoryKills(int max)
    {
        // Read recent kernel messages and filter here rather than with journalctl -g, whose PCRE grep is not on
        // every systemd version. -o short-iso puts a parseable timestamp at the start of each line.
        var lines = _Run("journalctl", "-k", "--no-pager", "-o", "short-iso", "-n", "4000");

        return lines
            .Where(line => OomMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .Reverse()
            .Take(max)
            .Select(line => new CrashLogEntry("Linux OOM (kernel log)", string.Empty, _KernelLineTime(line), line))
            .ToList();
    }

    // coredumpctl's time is "Fri 2026-07-17 14:00:00 CEST <pid> …" — drop the weekday and the timezone abbreviation
    // (neither parses) and read the date-time in the middle. Best-effort: an unrecognised format leaves it null
    // rather than inventing a time.
    private static DateTimeOffset? _CoredumpTime(string line)
    {
        var tokens = line.Split(' ');
        if (tokens.Length >= 3
            && DateTime.TryParse($"{tokens[1]} {tokens[2]}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return new DateTimeOffset(parsed);
        }

        return null;
    }

    // short-iso prefixes each line with e.g. "2026-07-17T14:00:00+0200".
    private static DateTimeOffset? _KernelLineTime(string line)
    {
        var firstSpace = line.IndexOf(' ');
        return firstSpace > 0
            && DateTimeOffset.TryParse(line.AsSpan(0, firstSpace), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
    }

    private static IReadOnlyList<string> _Run(string command, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var lines = new List<string>();
            while (process.StandardOutput.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            process.WaitForExit(2000);
            return lines;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The tool is not installed, or the read was refused: no artifacts to show, not a failure to report.
            return [];
        }
    }
}
