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

        var expected = _VmRssBytes(Environment.ProcessId);
        Assert.True(expected > 0, "this process must have a VmRSS to compare against");

        var row = new ProcProcessTableReader().Read().SingleOrDefault(row => row.ProcessId == Environment.ProcessId);

        Assert.NotNull(row);

        // Not exact equality: the two files are read a moment apart and a running test host allocates in between.
        // A field or page-size mistake is not a percent out — picking `stat`'s rss instead was 1.5% low, and a wrong
        // field index or a missing page-size multiply is orders out.
        Assert.InRange(row!.WorkingSetBytes, expected * 0.9, expected * 1.1);
    }

    /// <summary>
    /// The tree the resource panel walks needs a parent for every row, so a reader that returns rows without one
    /// would leave every session measuring only itself.
    /// </summary>
    [Fact]
    public void Read_GivesEveryRowAParentAndAName()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var rows = new ProcProcessTableReader().Read();

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.True(row.ProcessId > 0));
        Assert.Contains(rows, row => row.ParentProcessId > 0);
        Assert.Contains(rows, row => !string.IsNullOrWhiteSpace(row.Name));
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
