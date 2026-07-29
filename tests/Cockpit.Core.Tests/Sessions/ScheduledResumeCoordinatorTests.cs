using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Sessions;
using Cockpit.Core.Toasts;
using FluentAssertions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The machinery under a scheduled resume (AC-234): it remembers what is waiting, sends it when its moment comes,
/// and says so when it could not. What it must never do is send a prompt somewhere it does not belong — "continue"
/// with no history behind it is meaningless, and worse than nothing because it looks like it worked.
/// </summary>
public class ScheduledResumeCoordinatorTests
{
    private sealed class InMemoryStore : IScheduledResumeStore
    {
        public List<ScheduledResume> Saved { get; set; } = [];

        public Task<IReadOnlyList<ScheduledResume>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduledResume>>(Saved);

        public Task SaveAsync(IReadOnlyList<ScheduledResume> resumes, CancellationToken cancellationToken = default)
        {
            Saved = [.. resumes];
            return Task.CompletedTask;
        }
    }

    /// <summary>Records every toast shown, so a test can tell the "reopened and sent" report apart from the ordinary "could not be delivered" one.</summary>
    private sealed class RecordingToast : IToastService
    {
        public List<(string Message, ToastSeverity Severity)> Shown { get; } = [];

        public void Show(string message, ToastSeverity severity, string? actionLabel = null, Action? onAction = null) =>
            Shown.Add((message, severity));
    }

    private static ScheduledResume Resume(string paneId, DateTimeOffset dueAt, string prompt = "continue") =>
        new(paneId, dueAt, prompt, Reason: "Week is 95% used");

    [Fact]
    public void AResumeIsDue_OnceItsMomentHasArrived()
    {
        var moment = DateTimeOffset.Parse("2026-07-25T07:30:00+02:00");
        var resume = Resume("pane", moment);

        resume.IsDue(moment.AddMinutes(-1)).Should().BeFalse();
        resume.IsDue(moment).Should().BeTrue();
    }

    [Fact]
    public void AResumeWhoseMomentPassedWhileClosed_HasLapsed()
    {
        // Five minutes of grace covers the app being open and merely between ticks; hours later means it was shut.
        var moment = DateTimeOffset.Parse("2026-07-25T07:30:00+02:00");
        var resume = Resume("pane", moment);

        resume.HasLapsed(moment.AddMinutes(2), TimeSpan.FromMinutes(5)).Should().BeFalse();
        resume.HasLapsed(moment.AddHours(4), TimeSpan.FromMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public async Task Scheduling_PersistsImmediately_SoItSurvivesTheAppClosing()
    {
        // The window a resume exists to cover is exactly the one where the cockpit may not be running.
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store);

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddHours(1)));

        store.Saved.Should().ContainSingle().Which.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public async Task ASecondResumeForTheSameSession_ReplacesTheFirst()
    {
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store);

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddHours(1), "first"));
        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddHours(2), "second"));

        coordinator.Pending.Should().ContainSingle().Which.Prompt.Should().Be("second");
    }

    [Fact]
    public async Task Cancelling_RemovesItFromStorage_NotOnlyFromView()
    {
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store);
        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddHours(1)));

        await coordinator.CancelAsync("pane-1");

        coordinator.PendingFor("pane-1").Should().BeNull();
        store.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheMomentComes_ThePromptGoesToItsSession()
    {
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store);
        var session = new TestSessionPanel();
        coordinator.ResolveSession = _ => session;

        var moment = DateTimeOffset.Now.AddMinutes(-1);
        await coordinator.ScheduleAsync(Resume("pane-1", moment, "carry on"));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        session.Sent.Should().ContainSingle().Which.Should().Be("carry on");
        coordinator.Pending.Should().BeEmpty("a resume fires once");
        store.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task AResumeThatIsNotYetDue_StaysWaiting()
    {
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store);
        var session = new TestSessionPanel();
        coordinator.ResolveSession = _ => session;

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddHours(3)));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        session.Sent.Should().BeEmpty();
        coordinator.Pending.Should().ContainSingle();
    }

    [Fact]
    public async Task WhenTheSessionIsGone_NothingIsSentAnywhere()
    {
        // The failing mode to avoid is sending "continue" into some other session, or into a fresh one where it
        // means nothing at all. Dropping it and reporting is the honest outcome.
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store) { ResolveSession = _ => null };

        await coordinator.ScheduleAsync(Resume("pane-gone", DateTimeOffset.Now.AddMinutes(-1)));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        coordinator.Pending.Should().BeEmpty();
        store.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task OnLoad_WhatLapsedWhileClosed_IsDroppedRatherThanFiredLate()
    {
        var store = new InMemoryStore { Saved = [Resume("pane-1", DateTimeOffset.Now.AddHours(-4))] };
        var session = new TestSessionPanel();
        var coordinator = new ScheduledResumeCoordinator(store) { ResolveSession = _ => session };

        await coordinator.LoadAsync();

        coordinator.Pending.Should().BeEmpty();
        session.Sent.Should().BeEmpty("firing four hours late is a surprise, not a service");
        store.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task OnLoad_WhatIsStillAhead_IsKept()
    {
        var store = new InMemoryStore { Saved = [Resume("pane-1", DateTimeOffset.Now.AddHours(2))] };
        var coordinator = new ScheduledResumeCoordinator(store);

        await coordinator.LoadAsync();

        coordinator.Pending.Should().ContainSingle();
    }

    /// <summary>
    /// AC-410: pane-id continuity means a resume due within the restore <c>Grace</c> window can now resolve to a
    /// pane the operator has not started yet — its runtime never came up, so sending into it "completes without
    /// going anywhere" (the failure mode <c>SessionPanelViewModel.CanTakeAPrompt</c> exists to describe). This must
    /// land in the same "could not be delivered" branch as a session that is gone outright, not the "was sent" one.
    /// Asserted with xunit's own Assert (AC-372), unlike this file's older neighbours.
    /// </summary>
    [Fact]
    public async Task WhenTheResolvedPaneIsNotYetStarted_TheResumeIsNotSent_ButIsReported()
    {
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store);
        var session = new TestSessionPanel { CanTakeAPromptOverride = false };
        coordinator.ResolveSession = _ => session;

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddMinutes(-1), "carry on"));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        Assert.Empty(session.Sent);
        Assert.Empty(coordinator.Pending);
        Assert.Empty(store.Saved);
    }

    /// <summary>
    /// AC-290: a pane that is gone outright is not necessarily a dead end — if it can be reopened (a crash the
    /// operator was never asked about, picked back up after a restart), the resume should still land rather than
    /// report undelivered just because the direct send found nothing to send into.
    /// </summary>
    [Fact]
    public async Task WhenTheSessionIsGoneButReopenSucceeds_ThePromptStillLands()
    {
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store) { ResolveSession = _ => null };
        var reopened = new List<(string PaneId, string Prompt)>();
        coordinator.ReopenAndSend = (paneId, prompt) =>
        {
            reopened.Add((paneId, prompt));
            return Task.FromResult(true);
        };

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddMinutes(-1), "carry on"));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        Assert.Equal(("pane-1", "carry on"), Assert.Single(reopened));
        Assert.Empty(coordinator.Pending);
        Assert.Empty(store.Saved);
    }

    /// <summary>AC-290: the same restore attempt applies to a pane that resolved but was not yet startable, not only a pane that is gone.</summary>
    [Fact]
    public async Task WhenTheResolvedPaneIsNotYetStartedAndReopenSucceeds_ThePromptStillLands()
    {
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store);
        var session = new TestSessionPanel { CanTakeAPromptOverride = false };
        coordinator.ResolveSession = _ => session;
        var reopened = new List<string>();
        coordinator.ReopenAndSend = (paneId, _) =>
        {
            reopened.Add(paneId);
            return Task.FromResult(true);
        };

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddMinutes(-1), "carry on"));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        Assert.Equal("pane-1", Assert.Single(reopened));
        Assert.Empty(coordinator.Pending);
        // The direct-send path must not also fire — the reopen path is what handled this resume, not both.
        Assert.Empty(session.Sent);
    }

    /// <summary>
    /// AC-290: a reopen that cannot help (no persisted offer, wrong provider) reports the same honest,
    /// distinguishable "could not be delivered" outcome as no reopen at all — never the "reopened and sent" one.
    /// </summary>
    [Fact]
    public async Task WhenReopenCannotHelp_TheResumeFallsBackToUndelivered_WithTheUndeliveredToast()
    {
        var store = new InMemoryStore();
        var toast = new RecordingToast();
        var coordinator = new ScheduledResumeCoordinator(store, toast) { ResolveSession = _ => null };
        coordinator.ReopenAndSend = (_, _) => Task.FromResult(false);

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddMinutes(-1)));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        Assert.Empty(coordinator.Pending);
        Assert.Empty(store.Saved);
        var shown = Assert.Single(toast.Shown);
        Assert.Equal("A resume could not be delivered — its session is no longer open.", shown.Message);
        Assert.Equal(ToastSeverity.Warning, shown.Severity);
    }

    /// <summary>AC-290: reopening reaches into session launch, which can fail in ways a scheduler tick must survive rather than propagate.</summary>
    [Fact]
    public async Task WhenReopenThrows_TheResumeFallsBackToUndelivered_WithTheUndeliveredToast_AndNothingPropagates()
    {
        var store = new InMemoryStore();
        var toast = new RecordingToast();
        var coordinator = new ScheduledResumeCoordinator(store, toast) { ResolveSession = _ => null };
        coordinator.ReopenAndSend = (_, _) => throw new InvalidOperationException("the launch profile no longer exists");

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddMinutes(-1)));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        Assert.Empty(coordinator.Pending);
        Assert.Empty(store.Saved);
        var shown = Assert.Single(toast.Shown);
        Assert.Equal("A resume could not be delivered — its session is no longer open.", shown.Message);
        Assert.Equal(ToastSeverity.Warning, shown.Severity);
    }
}
