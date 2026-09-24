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
using Microsoft.Extensions.Logging;
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

    // `OnSave`/`OnLoad` let a test fail one specific call without a genuinely async store behind it.
    private sealed class FailableStore(params ScheduledResume[] stored) : IScheduledResumeStore
    {
        private List<ScheduledResume> _saved = [.. stored];

        public Func<Task>? OnSave { get; set; }

        public Func<Task>? OnLoad { get; set; }

        public async Task<IReadOnlyList<ScheduledResume>> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (OnLoad is { } fail)
            {
                await fail();
            }

            return _saved;
        }

        public async Task SaveAsync(IReadOnlyList<ScheduledResume> resumes, CancellationToken cancellationToken = default)
        {
            if (OnSave is { } fail)
            {
                await fail();
            }

            _saved = [.. resumes];
        }
    }

    private static ISessionHandle _Handle(List<string> sent)
    {
        var handle = Substitute.For<ISessionHandle>();
        handle.CanTakeAPrompt.Returns(true);
        handle.SendPromptAsync(Arg.Do<string>(sent.Add)).Returns(true);
        return handle;
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

        using var coordinator = new ScheduledResumeCoordinator(new FailableStore(), toast: null, logger: null, interval, clock)
        {
            ResolveSession = _ => handle,
        };

        await coordinator.StartAsync();
        await coordinator.ScheduleAsync(new ScheduledResume("pane-1", DateTimeOffset.Now.AddMinutes(-1), "carry on", Reason: null));
        var baseline = sent;

        clock.Advance(tick ? interval : interval - TimeSpan.FromSeconds(1));

        Assert.Equal(tick ? 1 : 0, sent - baseline);
    }

    // AC-1380: ported off the deleted `ScheduledResumeTimerTests` (App.ViewTests), onto the fake clock. What that
    // file also proved — a DispatcherTimer built off the UI thread never ticks at all — no longer applies.
    [Fact]
    public async Task ScheduledResumeCoordinator_ATickThatThrows_IsSurvived_AndTheSendAlreadyLanded()
    {
        var clock = new ManualClock();
        var interval = TimeSpan.FromSeconds(30);
        var sent = new List<string>();
        var due = new ScheduledResume("pane-1", DateTimeOffset.Now.AddMinutes(-1), "carry on", Reason: null);
        var store = new FailableStore(due) { OnSave = () => throw new IOException("the config file was locked") };
        var logger = new CountingLogger();

        using var coordinator = new ScheduledResumeCoordinator(store, toast: null, logger, interval, clock)
        {
            ResolveSession = _ => _Handle(sent),
        };

        await coordinator.StartAsync();
        clock.Advance(interval);

        Assert.Equal("carry on", Assert.Single(sent));
        Assert.IsType<IOException>(logger.FirstError);
    }

    [Fact]
    public async Task ScheduledResumeCoordinator_AStartThatFailed_CanBeStartedAgain()
    {
        // A config file held open for a moment, or one that does not parse, makes the load throw. If the claim on
        // "already started" survives that, the scheduler is off for the rest of the run and says it is running —
        // which is the exact shape of failure this ticket exists to remove.
        var clock = new ManualClock();
        var interval = TimeSpan.FromSeconds(30);
        var sent = new List<string>();
        var due = new ScheduledResume("pane-1", DateTimeOffset.Now.AddMinutes(-1), "carry on", Reason: null);
        var store = new FailableStore(due) { OnLoad = () => throw new IOException("the config file was locked") };

        using var coordinator = new ScheduledResumeCoordinator(store, toast: null, logger: null, interval, clock)
        {
            ResolveSession = _ => _Handle(sent),
        };

        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync());

        store.OnLoad = null;
        await coordinator.StartAsync();
        clock.Advance(interval);

        Assert.Equal("carry on", Assert.Single(sent));
    }

    [Fact]
    public async Task ScheduledResumeCoordinator_TwoStartsThatOverlap_OnlyOneOfThemStarts()
    {
        var logger = new CountingLogger();
        using var coordinator = new ScheduledResumeCoordinator(new FailableStore(), toast: null, logger, TimeSpan.FromSeconds(30), new ManualClock());

        await Task.WhenAll(coordinator.StartAsync(), coordinator.StartAsync());

        Assert.Equal(1, logger.Started);
    }

    private sealed class CountingLogger : ILogger<ScheduledResumeCoordinator>
    {
        public int Started { get; private set; }

        public Exception? FirstError { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("Scheduled resumes are running"))
            {
                Started++;
            }

            FirstError ??= exception;
        }
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
