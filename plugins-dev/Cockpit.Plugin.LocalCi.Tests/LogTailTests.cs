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
}
