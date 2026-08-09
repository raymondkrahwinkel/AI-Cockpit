using System.Runtime.InteropServices;
using Cockpit.Infrastructure.Diagnostics;

namespace Cockpit.Infrastructure.Tests.Diagnostics;

/// <summary>
/// The `/proc` reader against the kernel it reads, since that is the only thing that can say whether the fields were
/// picked apart correctly — there is no seam to hand it a fake `/proc`, and a fake would only prove the parser agrees
/// with the fixture the same hand wrote.
/// </summary>
public class ProcProcessTableReaderTests
{
    /// <summary>
    /// Resident memory comes from `statm`'s second field rather than a line scan through `status` (#78 follow-up):
    /// this runs for every process on the machine every couple of seconds, and `status` costs some fifty lines to
    /// reach one of them. The swap is only allowed to be cheaper, never to move the number, so this pins it against
    /// the `VmRSS` the old path read — the same field an operator would check with `ps`.
    /// </summary>
    [Fact]
    public void Read_ReportsTheSameResidentMemoryAsVmRss_ForThisVeryProcess()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        // Bracketed rather than compared against one reading: the test host is live and the rest of this assembly's
        // collections run beside it, so its own RSS moves between two reads — the SkiaSharp rasterisation tests alone
        // shift it by more than a tight band would allow. Taking VmRSS either side of the call gives a window the
        // reader's answer has to fall inside, which drifts with the process instead of pretending it stands still.
        var before = _VmRssBytes(Environment.ProcessId);
        var row = new ProcProcessTableReader().Read().SingleOrDefault(row => row.ProcessId == Environment.ProcessId);
        var after = _VmRssBytes(Environment.ProcessId);

        Assert.True(before > 0 && after > 0, "this process must have a VmRSS to compare against");
        Assert.NotNull(row);

        // The mistakes this guards against are not a few percent: reading statm's first field instead of its second
        // reports the whole address space, and forgetting the page-size multiply is out by four thousand.
        Assert.InRange(row!.WorkingSetBytes, Math.Min(before, after) * 0.8, Math.Max(before, after) * 1.2);
    }

    /// <summary>
    /// The parent is what the resource panel walks a session's tree by, so it has to be the real one — measured
    /// against `/proc/self/stat`'s own ppid rather than against "is positive". Positive is what a field index off by
    /// one still gives you: the neighbouring fields are pgrp, session and tty, all positive on hundreds of rows, so
    /// a shifted parse would leave this green while every session's tree quietly measured the wrong processes.
    /// </summary>
    [Fact]
    public void Read_ReportsTheRealParent_ForThisVeryProcess()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var rows = new ProcProcessTableReader().Read();
        var row = rows.SingleOrDefault(row => row.ProcessId == Environment.ProcessId);

        Assert.NotNull(row);
        Assert.Equal(_ParentProcessId(Environment.ProcessId), row!.ParentProcessId);

        // Every process has a comm, so this holds for all of them — one non-blank name out of six hundred would not
        // have said anything about the parse.
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Name)));
    }

    // The fourth field of /proc/<pid>/stat, counted after the comm — which is parenthesised and may itself contain
    // spaces, so the fields are taken from after the closing bracket rather than by splitting the whole line.
    private static int _ParentProcessId(int processId)
    {
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        var afterName = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');

        return int.Parse(afterName[1]);
    }

    private static long _VmRssBytes(int processId)
    {
        foreach (var line in File.ReadLines($"/proc/{processId}/status"))
        {
            if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kilobytes) ? kilobytes * 1024 : 0;
        }

        return 0;
    }
}
