using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Notifications;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Ci;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Backend.Tests;

/// <summary>
/// AC-1380 acceptance 1: each of the seven planners now ticks off an injected <see cref="TimeProvider"/> rather than
/// Avalonia's <c>DispatcherTimer</c>. Every test here advances a fake clock past one interval and counts the one
/// action that interval buys — and, in the same breath, that advancing short of it buys nothing.
/// </summary>
public class PlannerTimerTests
{
    // A clock whose timers fire only when the test advances it, honouring Change and Dispose as the real ones do —
    // the same shape SessionHostTests already proves this pattern with (F1.4).
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            while (_timers.Where(timer => timer.Due <= _now).MinBy(timer => timer.Due) is { } due)
            {
                due.Fire();
            }
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                _period = period;
                return true;
            }

            public void Fire()
            {
                Due = _period == Timeout.InfiniteTimeSpan ? null : Due + _period;
                callback(state);
            }

            public void Dispose() => Due = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class InMemoryScheduledResumeStore : IScheduledResumeStore
    {
        public Task<IReadOnlyList<ScheduledResume>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduledResume>>([]);

        public Task SaveAsync(IReadOnlyList<ScheduledResume> resumes, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static readonly TimeSpan CiInterval = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CiWatcher_TicksOncePerInterval_AndNeverBeforeIt(bool tick)
    {
        var clock = new ManualClock();
        var probes = 0;
        var settings = Substitute.For<INotificationSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new NotificationSettings { NotifyOnCiFailure = true });
        using var watcher = new CiWatcher(Substitute.For<IAttentionNotifier>(), Substitute.For<IAgentMessageInbox>(), settings, null, clock)
        {
            Watching = () => [new WatchedCheckout("pane-1", "AC-1380", Environment.CurrentDirectory)],
            Probe = (_, _) => { probes++; return Task.FromResult(string.Empty); },
            // Left at its real default, HeadProbe shells out to `git rev-parse HEAD` — genuinely async, which would
            // leave `_looking` claimed across the tick this test drives synchronously.
            HeadProbe = (_, _) => Task.FromResult(string.Empty),
        };

        watcher.Start();
        var baseline = probes;

        clock.Advance(tick ? CiInterval : CiInterval - TimeSpan.FromSeconds(1));

        Assert.Equal(tick ? 1 : 0, probes - baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InboxWakeScheduler_TicksOncePerInterval_AndNeverBeforeIt(bool tick)
    {
        var clock = new ManualClock();
        var interval = TimeSpan.FromSeconds(30);
        var looks = 0;
        var inbox = Substitute.For<IAgentMessageInbox>();
        inbox.PeekOldest(Arg.Any<string>()).Returns(_ => { looks++; return null; });
        using var scheduler = new InboxWakeScheduler(inbox, Substitute.For<IWorkspaceAgentGateway>(), null, clock)
        {
            Panes = () => ["pane-1"],
        };

        scheduler.Start();
        var baseline = looks;

        clock.Advance(tick ? interval : interval - TimeSpan.FromSeconds(1));

        Assert.Equal(tick ? 1 : 0, looks - baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleClaimReaper_TicksOncePerInterval_AndNeverBeforeIt(bool tick)
    {
        var clock = new ManualClock();
        var interval = TimeSpan.FromMinutes(15);
        var sweeps = 0;
        var claimsAudit = Substitute.For<IAgentResourceClaimsAudit>();
        claimsAudit.ListAll().Returns(_ => { sweeps++; return []; });
        using var reaper = new StaleClaimReaper(claimsAudit, Substitute.For<IAgentResourceClaims>(), Substitute.For<IAgentMessageInbox>(), null, clock)
        {
            LivePaneIds = () => ["pane-1"],
        };

        reaper.Start();
        var baseline = sweeps;

        clock.Advance(tick ? interval : interval - TimeSpan.FromSeconds(1));

        Assert.Equal(tick ? 1 : 0, sweeps - baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorktreeReconciler_TicksOncePerInterval_AndNeverBeforeIt(bool tick)
    {
        var clock = new ManualClock();
        var interval = TimeSpan.FromMinutes(15);
        var sweeps = 0;
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ReconcileAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { sweeps++; return Task.CompletedTask; });
        using var reconciler = new WorktreeReconciler(worktrees, null, clock)
        {
            LiveSessionIds = () => ["pane-1"],
        };

        reconciler.Start();
        var baseline = sweeps;

        clock.Advance(tick ? interval : interval - TimeSpan.FromSeconds(1));

        Assert.Equal(tick ? 1 : 0, sweeps - baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepotSyncWatcher_TicksOncePerInterval_AndNeverBeforeIt(bool tick)
    {
        var clock = new ManualClock();
        var interval = TimeSpan.FromMinutes(15);
        var checks = 0;
        var source = Substitute.For<ISharedProjectSource>();
        source.PrepareBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { checks++; return Task.FromResult(SharedProjectBindingResult.Failed("n/a")); });
        using var watcher = new DepotSyncWatcher(null, clock)
        {
            BoundProjects = () => [new DepotBoundProject("proj-1", source, "depot:proj-1")],
        };

        watcher.Start();
        var baseline = checks;

        clock.Advance(tick ? interval : interval - TimeSpan.FromSeconds(1));

        Assert.Equal(tick ? 1 : 0, checks - baseline);
    }

    // AC-1380 acceptance 2: the resume still resolves and sends through the session registry's handle, not through a
    // view model — this would not compile in Infrastructure if it still needed one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledResumeCoordinator_TicksOncePerInterval_AndSendsThroughTheHandle(bool tick)
    {
        var clock = new ManualClock();
        var interval = TimeSpan.FromSeconds(30);
        var sent = 0;
        var handle = Substitute.For<ISessionHandle>();
        handle.CanTakeAPrompt.Returns(true);
        handle.SendPromptAsync(Arg.Any<string>()).Returns(_ => { sent++; return Task.FromResult(true); });

        using var coordinator = new ScheduledResumeCoordinator(new InMemoryScheduledResumeStore(), toast: null, logger: null, interval, clock)
        {
            ResolveSession = _ => handle,
        };

        await coordinator.StartAsync();
        await coordinator.ScheduleAsync(new ScheduledResume("pane-1", DateTimeOffset.Now.AddMinutes(-1), "carry on", Reason: null));
        var baseline = sent;

        clock.Advance(tick ? interval : interval - TimeSpan.FromSeconds(1));

        Assert.Equal(tick ? 1 : 0, sent - baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionWatcher_TicksOncePerInterval_AndNeverBeforeIt(bool tick)
    {
        var clock = new ManualClock();
        var interval = TimeSpan.FromSeconds(30);
        var probes = 0;
        using var watcher = new SessionWatcher(Substitute.For<IAgentMessageInbox>(), null, clock)
        {
            Probe = (_, _) =>
            {
                probes++;
                return Task.FromResult<WatchedPane?>(new WatchedPane("pane-1", SessionStatus.Busy, false, true, 0, [], []));
            },
        };
        await watcher.WatchAsync("pane-1", [SessionWatchEvents.NeedsAttention], null, null);
        watcher.Start();
        var baseline = probes;

        clock.Advance(tick ? interval : interval - TimeSpan.FromSeconds(1));

        Assert.Equal(tick ? 1 : 0, probes - baseline);
    }
}
