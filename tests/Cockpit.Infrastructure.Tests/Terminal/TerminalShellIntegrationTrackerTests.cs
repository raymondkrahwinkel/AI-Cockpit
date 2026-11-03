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
    public void OnlyShellMarksTrackThePromptAndTheCurrentCommandsExitCode()
    {
        var tracker = new TerminalShellIntegrationTracker();

        tracker.Feed("$ ls\r\nfile-a  file-b\r\n$ ");

        Assert.False(tracker.ShellIntegrationSeen, "prompt-looking text is not a mark, and guessing from it is what this avoids");
        Assert.False(tracker.AtPrompt);
        Assert.Equal(0, tracker.CommandsFinished);
        tracker.Feed(Mark("A") + "raymond@box $ " + Mark("B"));
        Assert.True(tracker.ShellIntegrationSeen);
        Assert.True(tracker.AtPrompt);

        tracker.Feed(Mark("C") + "building...\r\n");
        Assert.False(tracker.AtPrompt, "a command is running, so this is also when a full-screen program would be open");
        tracker.Feed(Mark("B") + Mark("C") + "boom\r\n" + Mark("D;1"));

        Assert.Equal(1, tracker.CommandsFinished);
        Assert.Equal(1, tracker.LastExitCode);
        Assert.False(tracker.AtPrompt, "the next prompt mark is what says it is ready again");
        // The pty flushes on its own schedule, so a mark arriving in halves is normal.
        var split = Mark("D");
        tracker.Feed(Mark("C") + split[..4]);
        Assert.Equal(1, tracker.CommandsFinished);
        tracker.Feed(split[4..]);

        Assert.Equal(2, tracker.CommandsFinished);
        Assert.Null(tracker.LastExitCode);
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
}
