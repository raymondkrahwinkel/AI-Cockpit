using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

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
    public void ABackgroundCall_IsMarkedWhenItStarts_NotOnlyOnceItsResultArrives() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Call("""{"command":"sleep 40","run_in_background":true}"""));

        // Nothing has come back yet — the flag on the request is the whole basis for the badge here.
        var row = ToolRow(session);
        Assert.True(row.IsBackgroundTool);
        Assert.Equal("Background · running", row.BackgroundStatusText);
    });

    [Fact]
    public void ABlockingCall_CarriesNoBadge() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Call("""{"command":"dotnet build"}"""));
        session.Apply(Result("Build succeeded."));

        Assert.False(ToolRow(session).IsBackgroundTool);
    });

    [Fact]
    public void ARunningTask_ReadsAsRunning_AndAsDoneOnceTheLedgerStopsReportingIt() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Call("""{"command":"sleep 40","run_in_background":true}"""));
        session.Apply(Outstanding(new BackgroundTask("bu889nu9c", BackgroundTaskKind.Shell, "sleep 40")));
        session.Apply(Result("Command running in background with ID: bu889nu9c. Output is being written to: /tmp/x."));

        var row = ToolRow(session);
        Assert.Equal("bu889nu9c", row.BackgroundTaskId);
        Assert.Equal("Background · running", row.BackgroundStatusText);

        // How a task ending arrives: the whole set restated, this one no longer in it.
        session.Apply(Outstanding());

        Assert.Equal("Background · done", row.BackgroundStatusText);
    });

    [Fact]
    public void AToolMovedToTheBackgroundMidCall_IsMarkedFromItsOwnResult() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        // An MCP tool that overran (AC-1053): nothing in the input asked for this, so the hand-off sentence in
        // the result is the only place the task id is named.
        session.Apply(new ToolUseRequested
        {
            SessionId = "s1",
            ToolUseId = ToolUseId,
            ToolName = "mcp__cockpit-local-ci__run_local_checks",
            InputJson = """{"job":"build"}""",
        });
        Assert.False(ToolRow(session).IsBackgroundTool);

        session.Apply(Outstanding(new BackgroundTask("kswv2rq5q", BackgroundTaskKind.Unknown, "run_local_checks")));
        session.Apply(Result("still running after 120s. It was moved to the background as task kswv2rq5q"));

        var row = ToolRow(session);
        Assert.True(row.IsBackgroundTool);
        Assert.Equal("kswv2rq5q", row.BackgroundTaskId);
        Assert.Equal("Background · running", row.BackgroundStatusText);
    });

    [Fact]
    public void ACallThatCameBackAnError_ReadsAsFailed() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Call("""{"command":"sleep 40","run_in_background":true}"""));
        session.Apply(Result("transport dropped mid-call; response was lost", isError: true));

        Assert.Equal("Background · failed", ToolRow(session).BackgroundStatusText);
    });

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
