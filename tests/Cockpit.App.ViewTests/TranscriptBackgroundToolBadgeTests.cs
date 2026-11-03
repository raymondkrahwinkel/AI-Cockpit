using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.App.Composition;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The background badge on a tool-use row (AC-1056). A call the session let go of and one it is waiting on used
/// to render identically, so an operator could not check what a session claimed it had done.
/// </summary>
[Collection("avalonia")]
public class TranscriptBackgroundToolBadgeTests
{
    private const string ToolUseId = "t1";

    private static ToolUseRequested Call(string inputJson) =>
        new() { SessionId = "s1", ToolUseId = ToolUseId, ToolName = "Bash", InputJson = inputJson };

    private static ToolResult Result(string content, bool isError = false) =>
        new() { SessionId = "s1", ToolUseId = ToolUseId, Content = content, IsError = isError };

    private static BackgroundTasksChanged Outstanding(params BackgroundTask[] tasks) =>
        new() { SessionId = "s1", Tasks = tasks };

    // The parameterless constructor seeds design-time sample rows, so the row under test is found by its own id.
    private static TranscriptEntryViewModel ToolRow(SessionViewModel session) =>
        session.Transcript.Single(row => row.Kind == TranscriptEntryKind.ToolUse && row.ToolUseId == ToolUseId);

    [Fact]
    public void ATaskThatFailsAfterBackgrounding_ReadsAsFailed_NotJustDone() => HeadlessAvalonia.Run(() =>
    {
        // AC-1057: before the notification existed, the ledger dropping a task id read as "done" whether the task
        // succeeded or blew up — this is the exact case that used to hide a failure as a checkmark.
        var session = new SessionViewModel();

        session.Apply(Call("""{"command":"false","run_in_background":true}"""));
        session.Apply(Outstanding(new BackgroundTask("b2n9en4yr", BackgroundTaskKind.Shell, "false")));
        session.Apply(Result("Command running in background with ID: b2n9en4yr. Output is being written to: /tmp/x."));
        session.Apply(Outstanding());
        session.Apply(new BackgroundTaskNotification { SessionId = "s1", TaskId = "b2n9en4yr", ToolUseId = ToolUseId, Status = BackgroundTaskStatus.Failed });

        Assert.Equal("Background · failed", ToolRow(session).BackgroundStatusText);
    });
}
