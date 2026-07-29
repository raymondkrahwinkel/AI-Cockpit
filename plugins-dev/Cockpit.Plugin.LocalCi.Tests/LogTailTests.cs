using Cockpit.Plugin.LocalCi.Execution;

namespace Cockpit.Plugin.LocalCi.Tests;

public class LogTailTests
{
    [Fact]
    public void ItKeepsTheEndAndDropsTheStart()
    {
        var tail = new LogTail(maxLines: 3, maxCharacters: 10_000);

        foreach (var line in new[] { "restore", "restore", "build", "FAILED: one test" })
        {
            tail.Add(line);
        }

        // The end is where the failure is; the start is restore output nobody reads.
        Assert.Equal(["restore", "build", "FAILED: one test"], tail.Text().Split(Environment.NewLine));
    }

    [Fact]
    public void ALineBudgetAloneWouldNotBoundIt()
    {
        var tail = new LogTail(maxLines: 100, maxCharacters: 40);

        tail.Add(new string('x', 200));
        tail.Add("FAILED: one test");

        Assert.Equal("FAILED: one test", tail.Text());
    }

    [Fact]
    public void OneOversizedLineIsStillKept()
    {
        var tail = new LogTail(maxLines: 100, maxCharacters: 10);

        tail.Add(new string('x', 200));

        // Trimming to nothing would turn "the log was long" into "there was no log", which reads as a run that
        // said nothing rather than one that said too much.
        Assert.Equal(200, tail.Text().Length);
    }

    [Fact]
    public void NothingInNothingOut() => Assert.Equal(string.Empty, new LogTail(10, 100).Text());

    [Fact]
    public async Task TwoWritersAtOnceIsTheOrdinaryCase()
    {
        var tail = new LogTail(maxLines: 50, maxCharacters: 4000);

        // A run has two: a process serves stdout and stderr as independent read loops, and act writes its progress
        // to one while the job writes to the other. Unsynchronised, the trimming loop corrupts the queue or throws
        // on a thread nobody is watching — which in .NET takes the whole app with it.
        await Task.WhenAll(
            Task.Run(() => _Fill(tail, "out")),
            Task.Run(() => _Fill(tail, "err")));

        var lines = tail.Text().Split(Environment.NewLine);
        Assert.Equal(50, lines.Length);
        Assert.All(lines, line => Assert.Matches("^(out|err) [0-9]+$", line));
    }

    private static void _Fill(LogTail tail, string stream)
    {
        for (var line = 0; line < 5000; line++)
        {
            tail.Add($"{stream} {line}");
        }
    }
}
