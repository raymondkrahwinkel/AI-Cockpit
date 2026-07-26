using Cockpit.Infrastructure.Terminal;
using FluentAssertions;

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

        tracker.ShellIntegrationSeen.Should().BeFalse("prompt-looking text is not a mark, and guessing from it is what this avoids");
        tracker.AtPrompt.Should().BeFalse();
        tracker.CommandsFinished.Should().Be(0);
    }

    [Fact]
    public void APromptMark_MeansTheShellIsIdle_AndACommandStartingTakesItOffThePrompt()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Mark("A") + "raymond@box $ " + Mark("B"));
        tracker.ShellIntegrationSeen.Should().BeTrue();
        tracker.AtPrompt.Should().BeTrue();

        tracker.Feed(Mark("C") + "building...\r\n");
        tracker.AtPrompt.Should().BeFalse("a command is running, so this is also when a full-screen program would be open");
    }

    [Fact]
    public void AFinishMark_CountsTheCommand_AndCarriesItsExitCode()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Mark("B") + Mark("C") + "boom\r\n" + Mark("D;1"));

        tracker.CommandsFinished.Should().Be(1);
        tracker.LastExitCode.Should().Be(1);
        tracker.AtPrompt.Should().BeFalse("the next prompt mark is what says it is ready again");
    }

    [Fact]
    public void AFinishMarkWithoutACode_CountsButReportsNoExitCode()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Mark("C") + Mark("D"));

        tracker.CommandsFinished.Should().Be(1);
        tracker.LastExitCode.Should().BeNull("reporting 0 here would invent a success the shell never claimed");
    }

    [Fact]
    public void AMarkSplitAcrossTwoWrites_IsStillSeen()
    {
        // The pty flushes on its own schedule, so a mark arriving in halves is normal — missing it would strand
        // run_in_terminal waiting for a command that already finished.
        var tracker = new TerminalShellIntegrationTracker();
        var mark = Mark("D;0");

        tracker.Feed("done\r\n" + mark[..4]);
        tracker.CommandsFinished.Should().Be(0);

        tracker.Feed(mark[4..]);
        tracker.CommandsFinished.Should().Be(1);
        tracker.LastExitCode.Should().Be(0);
    }

    [Fact]
    public void TheStTerminatedForm_IsReadTheSameAsTheBellTerminatedOne()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed($"{Escape}]133;D;7{Escape}\\");

        tracker.CommandsFinished.Should().Be(1);
        tracker.LastExitCode.Should().Be(7);
    }

    [Fact]
    public void VsCodesOsc633_IsUnderstoodToo()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed($"{Escape}]633;B{Bell}");

        tracker.AtPrompt.Should().BeTrue();
    }

    [Fact]
    public void OtherEscapeSequences_AreIgnoredWithoutDisturbingTheState()
    {
        var tracker = new TerminalShellIntegrationTracker();
        tracker.Feed(Mark("B"));

        // A colour run, a cursor move and a window-title OSC — all common, none of them ours.
        tracker.Feed($"{Escape}[32mgreen{Escape}[0m{Escape}[2K{Escape}]0;a title{Bell}");

        tracker.AtPrompt.Should().BeTrue();
        tracker.CommandsFinished.Should().Be(0);
    }

    [Fact]
    public void AMalformedOscDoesNotSwallowTheMarksAfterIt()
    {
        // A half-written OSC — a binary file catted, a nested session — used to wedge the scan: it waited forever for
        // a terminator that never came, and every real mark behind it went unread. run_in_terminal then hung to its
        // timeout on a command the shell had already finished.
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed($"{Escape}]133;B{Escape}]133;D;0{Bell}");

        tracker.ShellIntegrationSeen.Should().BeTrue();
        tracker.CommandsFinished.Should().Be(1);
        tracker.LastExitCode.Should().Be(0);
    }

    [Fact]
    public void ACommandStarting_IsCountedSeparatelyFromItFinishing()
    {
        // A finish on its own could belong to something already in flight; a caller waits for both to move.
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Mark("D;0"));
        tracker.CommandsStarted.Should().Be(0);
        tracker.CommandsFinished.Should().Be(1);

        tracker.Feed(Mark("C") + Mark("D;0"));
        tracker.CommandsStarted.Should().Be(1);
        tracker.CommandsFinished.Should().Be(2);
    }

    [Fact]
    public void AnEscapeThatNeverCompletes_DoesNotGrowWithoutBound()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed(Escape + "]133;" + new string('x', 4096));
        tracker.Feed(Mark("D;0"));

        tracker.CommandsFinished.Should().Be(1, "the runaway is dropped, and the next real mark is still read");
    }
}
