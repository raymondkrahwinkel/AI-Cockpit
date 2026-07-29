using Cockpit.Infrastructure.Terminal;

namespace Cockpit.Infrastructure.Tests.Terminal;

/// <summary>
/// Reading the shell's own OSC 133/633 marks (AC-34), which is what lets run_in_terminal know a command finished
/// instead of guessing at a quiet stream — including when the pty splits a mark across two writes.
/// </summary>
public class TerminalShellIntegrationTrackerTests
{
    private const string Escape = "\x1b";
    private const string Bell = "\a";

    private static string Mark(string payload) => $"{Escape}]133;{payload}{Bell}";

    [Fact]
    public void AShellThatSaysNothing_LeavesUsWithoutAnyClaimToMake()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed("$ ls\r\nfile-a  file-b\r\n$ ");

        Assert.False(tracker.ShellIntegrationSeen, "prompt-looking text is not a mark, and guessing from it is what this avoids");
        Assert.False(tracker.AtPrompt);
        Assert.Equal(0, tracker.CommandsFinished);
    }

    [Fact]
    public void APromptMark_MeansTheShellIsIdle_AndACommandStartingTakesItOffThePrompt()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Mark("A") + "raymond@box $ " + Mark("B"));
        Assert.True(tracker.ShellIntegrationSeen);
        Assert.True(tracker.AtPrompt);

        tracker.Feed(Mark("C") + "building...\r\n");
        Assert.False(tracker.AtPrompt, "a command is running, so this is also when a full-screen program would be open");
    }

    [Fact]
    public void AFinishMark_CountsTheCommand_AndCarriesItsExitCode()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Mark("B") + Mark("C") + "boom\r\n" + Mark("D;1"));

        Assert.Equal(1, tracker.CommandsFinished);
        Assert.Equal(1, tracker.LastExitCode);
        Assert.False(tracker.AtPrompt, "the next prompt mark is what says it is ready again");
    }

    [Fact]
    public void AFinishMarkWithoutACode_CountsButReportsNoExitCode()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Mark("C") + Mark("D"));

        Assert.Equal(1, tracker.CommandsFinished);
        Assert.Null(tracker.LastExitCode);
    }

    [Fact]
    public void AMarkSplitAcrossTwoWrites_IsStillSeen()
    {
        // The pty flushes on its own schedule, so a mark arriving in halves is normal — missing it would strand
        // run_in_terminal waiting for a command that already finished.
        var tracker = new TerminalShellIntegrationTracker();
        var mark = Mark("D;0");

        tracker.Feed("done\r\n" + mark[..4]);
        Assert.Equal(0, tracker.CommandsFinished);

        tracker.Feed(mark[4..]);
        Assert.Equal(1, tracker.CommandsFinished);
        Assert.Equal(0, tracker.LastExitCode);
    }

    [Fact]
    public void TheStTerminatedForm_IsReadTheSameAsTheBellTerminatedOne()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed($"{Escape}]133;D;7{Escape}\\");

        Assert.Equal(1, tracker.CommandsFinished);
        Assert.Equal(7, tracker.LastExitCode);
    }

    [Fact]
    public void VsCodesOsc633_IsUnderstoodToo()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed($"{Escape}]633;B{Bell}");

        Assert.True(tracker.AtPrompt);
    }

    [Fact]
    public void OtherEscapeSequences_AreIgnoredWithoutDisturbingTheState()
    {
        var tracker = new TerminalShellIntegrationTracker();
        tracker.Feed(Mark("B"));

        // A colour run, a cursor move and a window-title OSC — all common, none of them ours.
        tracker.Feed($"{Escape}[32mgreen{Escape}[0m{Escape}[2K{Escape}]0;a title{Bell}");

        Assert.True(tracker.AtPrompt);
        Assert.Equal(0, tracker.CommandsFinished);
    }

    [Fact]
    public void AMalformedOscDoesNotSwallowTheMarksAfterIt()
    {
        // A half-written OSC — a binary file catted, a nested session — used to wedge the scan: it waited forever for
        // a terminator that never came, and every real mark behind it went unread. run_in_terminal then hung to its
        // timeout on a command the shell had already finished.
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed($"{Escape}]133;B{Escape}]133;D;0{Bell}");

        Assert.True(tracker.ShellIntegrationSeen);
        Assert.Equal(1, tracker.CommandsFinished);
        Assert.Equal(0, tracker.LastExitCode);
    }

    [Fact]
    public void ACommandStarting_IsCountedSeparatelyFromItFinishing()
    {
        // A finish on its own could belong to something already in flight; a caller waits for both to move.
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Mark("D;0"));
        Assert.Equal(0, tracker.CommandsStarted);
        Assert.Equal(1, tracker.CommandsFinished);

        tracker.Feed(Mark("C") + Mark("D;0"));
        Assert.Equal(1, tracker.CommandsStarted);
        Assert.Equal(2, tracker.CommandsFinished);
    }

    [Fact]
    public void AnEscapeThatNeverCompletes_DoesNotGrowWithoutBound()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Escape + "]133;" + new string('x', 4096));
        tracker.Feed(Mark("D;0"));

        Assert.Equal(1, tracker.CommandsFinished);
    }
}
