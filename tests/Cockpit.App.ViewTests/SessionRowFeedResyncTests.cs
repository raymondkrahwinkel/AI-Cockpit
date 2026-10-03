using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Events;
using Cockpit.Tests.Shared;
using NSubstitute;
using Cockpit.App.Composition;

namespace Cockpit.App.ViewTests;

// AC-1438, layer 2 (data loss): a pane draws its rows from the backend event log, and a reader the log reset has missed
// some of them. No journey can force a reset, so the log is told to reset here; what the pane does next is the real path.
[Collection("avalonia")]
public sealed class SessionRowFeedResyncTests
{
    [Fact]
    public Task RowsTheReaderMissed_AreDrawnAfterTheLogResetIt_EachOnceAndInTheHostsOrder() => HeadlessAvalonia.RunAsync(async () =>
    {
        var log = new BackendEventLog();
        var session = TestSessions.Pane(Substitute.For<ISessionManager>(), eventLog: log);

        // The bridge's own shape (`SessionEventsBridge`), switched off for the stretch the reader misses.
        var publishing = true;
        session.Control.RowUpserted += upsert =>
        {
            if (publishing)
            {
                log.Append("row", session.PaneId, new { session.PaneId, upsert.Seq, upsert.Version, upsert.Row });
            }
        };

        session.Apply(new AssistantTextCompleted { SessionId = "s1", Text = "first" });
        await _RowsAsync(session, 1);

        publishing = false;
        session.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "missed", ToolName = "Bash", InputJson = "{}" });
        publishing = true;
        log.Append("reset", null, new { });
        session.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "after", ToolName = "Bash", InputJson = "{}" });
        await _RowsAsync(session, 3);

        Assert.Equal(
            [(TranscriptEntryKind.AssistantText, null), (TranscriptEntryKind.ToolUse, "missed"), (TranscriptEntryKind.ToolUse, "after")],
            session.Transcript.Select(row => (row.Kind, row.ToolUseId)));
    });

    // The pane draws on the UI thread after the reader hands a batch over, so this waits for the rows, not for a clock.
    private static async Task _RowsAsync(SessionViewModel session, int count)
    {
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (session.Transcript.Count >= count)
            {
                drawn.TrySetResult();
            }
        }

        session.Transcript.CollectionChanged += (_, _) => Check();
        Check();
        await drawn.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
