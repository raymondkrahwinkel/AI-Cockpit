using Cockpit.Core.Diagnostics;
using FluentAssertions;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// The two text formats the resource meter (#78) reads a process table out of. This is where the platform paths
/// can be checked without the platform — and for macOS it is the only place they can be: there is no Mac here, so
/// the parsing is proven and the rest (does <c>ps</c> run, does it take these flags) is stated as untested rather
/// than assumed.
/// </summary>
public class ProcessTableParsingTests
{
    [Fact]
    public void ProcStat_ReadsTheParentAndTheProcessorTime()
    {
        // pid (comm) state ppid ... utime(14) stime(15)
        var line = "1234 (claude) S 1000 1234 1000 0 -1 4194304 5000 0 0 0 250 130 0 0 20 0 12 0 999 0 0";

        var stat = ProcStatLine.Parse(line);

        stat.Should().NotBeNull();
        stat!.ParentProcessId.Should().Be(1000);
        stat.TotalTicks.Should().Be(380);
    }

    [Fact]
    public void ProcStat_WhenTheExecutableNameContainsSpacesAndParentheses_StillReadsTheRightFields()
    {
        // The trap in /proc/<pid>/stat: field 2 is the name in parentheses, and it may itself contain both. A
        // parser counting fields from the left reads garbage here — which is why we count after the LAST ')'.
        var line = "77 (my prog (v2) :)) S 5 77 5 0 -1 0 0 0 0 0 11 4 0 0 20 0 1 0 5 0 0";

        var stat = ProcStatLine.Parse(line);

        stat!.ParentProcessId.Should().Be(5);
        stat.TotalTicks.Should().Be(15);
    }

    [Fact]
    public void ProcStat_OfSomethingThatIsNotAStatLine_IsNothing()
    {
        ProcStatLine.Parse("nonsense").Should().BeNull();
        ProcStatLine.Parse(string.Empty).Should().BeNull();
    }

    [Fact]
    public void PsLine_ReadsPidParentCpuTimeAndResidentMemory()
    {
        // pid ppid time rss(kB)
        var row = PsLine.Parse("  501   1 12:34.56  204800");

        row.Should().NotBeNull();
        row!.ProcessId.Should().Be(501);
        row.ParentProcessId.Should().Be(1);
        row.CpuTime.Should().Be(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(34.56));
        row.WorkingSetBytes.Should().Be(204800L * 1024);
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

        PsLine.ParseCpuTime(value).Should().Be(expected);
    }

    [Fact]
    public void PsLine_OfAHeaderOrRubbish_IsNothing()
    {
        PsLine.Parse("PID PPID TIME RSS").Should().BeNull();
        PsLine.Parse(string.Empty).Should().BeNull();
    }
}
