using System.Diagnostics;
using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

// macOS's process table, via `ps` (#78): no `/proc`, and .NET exposes no parent-process id, so `ps` is the
// one reliable source. Parsing lives in `PsLine` and is unit-tested — this codebase has no Mac to try it
// on, so only what can be verified without one is verified; whether `ps` itself runs is assumed.
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
