using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
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

    private static ScheduledResume Resume(string paneId, DateTimeOffset dueAt, string prompt = "continue") =>
        new(paneId, ConversationId: null, dueAt, prompt, Reason: "Week is 95% used");

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
}
