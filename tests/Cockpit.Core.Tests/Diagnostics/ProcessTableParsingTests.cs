using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// The two text formats the resource meter (#78) reads a process table out of. This is where the platform paths
/// can be checked without the platform — and for macOS it is the only place they can be: there is no Mac here, so
/// the parsing is proven and the rest (does <c>ps</c> run, does it take these flags) is stated as untested rather
/// than assumed.
/// </summary>
public class ProcessTableParsingTests
{
    // pid (comm) state ppid ... utime(14) stime(15). The second row is the trap in /proc/&lt;pid&gt;/stat: field 2 is
    // the name in parentheses, and it may itself contain both spaces and parentheses. A parser counting fields from
    // the left reads garbage there — which is why we count after the LAST ')'.
    [Theory]
    [InlineData("1234 (claude) S 1000 1234 1000 0 -1 4194304 5000 0 0 0 250 130 0 0 20 0 12 0 999 0 0", 1000, 380)]
    [InlineData("77 (my prog (v2) :)) S 5 77 5 0 -1 0 0 0 0 0 11 4 0 0 20 0 1 0 5 0 0", 5, 15)]
    public void ProcStat_ReadsTheParentAndTheProcessorTime(string line, int expectedParent, long expectedTicks)
    {
        var stat = ProcStatLine.Parse(line);

        Assert.NotNull(stat);
        Assert.Equal(expectedParent, stat.ParentProcessId);
        Assert.Equal(expectedTicks, stat.TotalTicks);
    }

    [Fact]
    public void ProcStat_OfSomethingThatIsNotAStatLine_IsNothing()
    {
        Assert.Null(ProcStatLine.Parse("nonsense"));
        Assert.Null(ProcStatLine.Parse(string.Empty));
    }

    [Fact]
    public void PsLine_ReadsPidParentCpuTimeAndResidentMemory()
    {
        // pid ppid time rss(kB)
        var row = PsLine.Parse("  501   1 12:34.56  204800");

        Assert.NotNull(row);
        Assert.Equal(501, row.ProcessId);
        Assert.Equal(1, row.ParentProcessId);
        Assert.Equal(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(34.56), row.CpuTime);
        Assert.Equal(204800L * 1024, row.WorkingSetBytes);
    }

    [Theory]
    [InlineData("00:12.30", 0, 0, 12.3)]
    [InlineData("01:02:03", 1, 2, 3)]
    [InlineData("2-03:04:05", 51, 4, 5)]
    public void PsLine_ReadsEveryShapeOfCpuTimeMacOsPrints(string value, int hours, int minutes, double seconds)
    {
        // ps switches format as a process ages: MM:SS.ss, then HH:MM:SS, then D-HH:MM:SS. Getting this wrong
        // would quietly under-report a long-running session by orders of magnitude.
        var expected = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);

        Assert.Equal(expected, PsLine.ParseCpuTime(value));
    }

    [Fact]
    public void PsLine_OfAHeaderOrRubbish_IsNothing()
    {
        Assert.Null(PsLine.Parse("PID PPID TIME RSS"));
        Assert.Null(PsLine.Parse(string.Empty));
    }
}
