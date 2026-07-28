using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// <see cref="SessionConversationTracker"/> (AC-408): the default <see cref="ISessionConversationSink"/>, kept
/// deliberately small — an in-memory dedupe per pane plus a <see cref="SessionConversationTracker.Changed"/>
/// event, with no store and no resume offer (a follow-up ticket's concern).
/// </summary>
public class SessionConversationTrackerTests
{
    [Fact]
    public void Report_RaisesChanged_ForThePaneAndConversationReported()
    {
        var tracker = new SessionConversationTracker();
        SessionConversationReported? received = null;
        tracker.Changed += reported => received = reported;

        tracker.Report("pane-1", SessionConversationId.Known("session-a"));

        Assert.Equal(new SessionConversationReported("pane-1", SessionConversationId.Known("session-a")), received);
    }

    [Fact]
    public void Report_DoesNotRaiseChanged_WhenTheSameConversationIsReportedAgain()
    {
        var tracker = new SessionConversationTracker();
        var raiseCount = 0;
        tracker.Changed += _ => raiseCount++;

        tracker.Report("pane-1", SessionConversationId.Known("session-a"));
        tracker.Report("pane-1", SessionConversationId.Known("session-a"));

        Assert.Equal(1, raiseCount);
    }

    [Fact]
    public void Report_RaisesChangedAgain_WhenThatPanesConversationIdActuallyChanges()
    {
        var tracker = new SessionConversationTracker();
        var received = new List<SessionConversationReported>();
        tracker.Changed += reported => received.Add(reported);

        tracker.Report("pane-1", SessionConversationId.Known("session-a"));
        tracker.Report("pane-1", SessionConversationId.Known("session-b"));

        Assert.Equal(2, received.Count);
        Assert.Equal(SessionConversationId.Known("session-b"), received[1].Conversation);
    }

    [Fact]
    public void Report_TracksEachPaneIndependently()
    {
        var tracker = new SessionConversationTracker();
        var received = new List<SessionConversationReported>();
        tracker.Changed += reported => received.Add(reported);

        tracker.Report("pane-1", SessionConversationId.Known("session-a"));
        tracker.Report("pane-2", SessionConversationId.Known("session-a"));

        // Same conversation id, but a different pane — both are genuine reports, not a duplicate.
        Assert.Equal(2, received.Count);
    }
}
