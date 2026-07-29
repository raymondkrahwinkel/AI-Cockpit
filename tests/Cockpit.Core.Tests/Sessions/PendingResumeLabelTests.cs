using Cockpit.App.Services;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The "Resuming Mon 13:12" line on a session (AC-368). It used to be written once, when the resume was scheduled,
/// and never touched again — so it stayed up after the resume had fired, after it had lapsed, and after it was
/// cancelled, while a resume that really was still waiting showed nothing at all after a restart. A banner that is
/// right only at the moment it appears is worse than none: it is the thing the operator checks instead of asking.
/// </summary>
/// <remarks>
/// Asserted with xunit's own Assert: the fluent library its neighbours use is on its way out (AC-372).
/// </remarks>
public class PendingResumeLabelTests
{
    private static ScheduledResume Resume(string paneId, DateTimeOffset dueAt) =>
        new(paneId, dueAt, "continue", Reason: "Week is 95% used");

    [Fact]
    public async Task SchedulingOne_PutsTheLineUp_WithoutAnybodySettingIt()
    {
        var coordinator = new ScheduledResumeCoordinator(new InMemoryScheduledResumeStore());
        var session = new TestSessionPanel { Resumes = coordinator };

        await coordinator.ScheduleAsync(Resume(session.PaneId, DateTimeOffset.Now.AddHours(1)));

        Assert.True(session.HasPendingResume);
        Assert.StartsWith("Resuming ", session.PendingResumeLabel);
    }

    [Fact]
    public async Task OnceItHasFired_TheLineGoesAway()
    {
        var coordinator = new ScheduledResumeCoordinator(new InMemoryScheduledResumeStore());
        var session = new TestSessionPanel { Resumes = coordinator };
        coordinator.ResolveSession = _ => session;

        await coordinator.ScheduleAsync(Resume(session.PaneId, DateTimeOffset.Now.AddMinutes(-1)));
        Assert.True(session.HasPendingResume);

        await coordinator.RunDueAsync(DateTimeOffset.Now);

        Assert.Equal(string.Empty, session.PendingResumeLabel);
    }

    [Fact]
    public async Task CancellingIt_TakesTheLineWithIt()
    {
        var coordinator = new ScheduledResumeCoordinator(new InMemoryScheduledResumeStore());
        var session = new TestSessionPanel { Resumes = coordinator };

        await coordinator.ScheduleAsync(Resume(session.PaneId, DateTimeOffset.Now.AddHours(1)));
        Assert.True(session.HasPendingResume, "there has to be a line before its going away means anything");

        await coordinator.CancelAsync(session.PaneId);

        Assert.Equal(string.Empty, session.PendingResumeLabel);
    }

    [Fact]
    public async Task ASessionHandedASchedulerThatAlreadyKnowsAboutIt_GetsItsLineWithoutWaitingForAnEvent()
    {
        // The order the startup path really has: the scheduler reads what was left over before the session it
        // belongs to exists, so no event is coming for that session and being handed the scheduler has to be
        // enough on its own.
        //
        // Note what this test does NOT claim: that a resume is actually delivered across a restart. A restored
        // pane keeps the id it was saved under (SessionPanelViewModel.AdoptPaneId, AC-410), so a stored resume can
        // find this pane again by id — but this test only checks that the pending line shows up, not that
        // RunDueAsync would send into it, which it will not until the pane has actually been started
        // (CanTakeAPrompt; see ScheduledResumeCoordinatorTests.WhenTheResolvedPaneIsNotYetStarted_...).
        var store = new InMemoryScheduledResumeStore();
        var session = new TestSessionPanel();
        Assert.Equal(string.Empty, session.PendingResumeLabel);

        var scheduler = new ScheduledResumeCoordinator(store);
        await scheduler.ScheduleAsync(Resume(session.PaneId, DateTimeOffset.Now.AddHours(2)));

        var loaded = new ScheduledResumeCoordinator(store);
        await loaded.LoadAsync();

        session.Resumes = loaded;

        Assert.True(session.HasPendingResume);
    }

    [Fact]
    public async Task AClosedSessionStopsListening_SoTheSchedulerDoesNotHoldItOpen()
    {
        // The scheduler is one singleton for the whole run; a panel that stays subscribed outlives its own window.
        // Asserting that Resumes went null would only read back the line in DisposeAsync that sets it — the claim
        // is that the handler is gone, so the test has to make the scheduler shout and show nobody answered.
        var coordinator = new ScheduledResumeCoordinator(new InMemoryScheduledResumeStore());
        var session = new TestSessionPanel { Resumes = coordinator };
        await coordinator.ScheduleAsync(Resume(session.PaneId, DateTimeOffset.Now.AddHours(1)));

        await session.DisposeAsync();
        Assert.Null(session.Resumes);

        const string Untouched = "left exactly as the closed session had it";
        session.PendingResumeLabel = Untouched;

        await coordinator.ScheduleAsync(Resume(session.PaneId, DateTimeOffset.Now.AddHours(3)));

        Assert.Equal(Untouched, session.PendingResumeLabel);
    }
}
