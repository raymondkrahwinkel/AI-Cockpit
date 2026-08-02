using System.Diagnostics;
using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

// macOS's process table, via `ps` (#78). macOS has no `/proc`, and .NET exposes no parent-process id,
// so the one reliable way to see who spawned what is the tool the system ships with. One `ps` per sample
// (every few seconds), not one per session.
//
// The parsing lives in `PsLine` and is unit-tested — this codebase has no Mac to try it on, so the
// part that can be verified without one is verified without one, and the part that cannot (does `ps` run,
// does it accept these flags) is stated plainly rather than assumed.
[SupportedOSPlatform("macos")]
internal sealed class PsProcessTableReader : IProcessTableReader
{
    public IReadOnlyList<ProcessRow> Read()
    {
        var startInfo = new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The '=' suffixes suppress the header, so every line is data.
        startInfo.ArgumentList.Add("-axo");
        startInfo.ArgumentList.Add("pid=,ppid=,time=,rss=,comm=");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var rows = new List<ProcessRow>();
            while (process.StandardOutput.ReadLine() is { } line)
            {
                if (PsLine.Parse(line) is { } row)
                {
                    rows.Add(row);
                }
            }

            process.WaitForExit(2000);
            return rows;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No ps, no meter — an empty table shows nothing rather than a wrong number.
            return [];
        }
    }
}
