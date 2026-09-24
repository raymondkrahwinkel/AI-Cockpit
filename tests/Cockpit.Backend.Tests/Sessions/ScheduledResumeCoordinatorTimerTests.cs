using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cockpit.Backend.Tests.Sessions;

/// <summary>
/// AC-1380: the timer guarantees <c>ScheduledResumeTimerTests</c> (App.ViewTests) proved against a real Avalonia
/// dispatcher — a tick surviving an exception, a failed start being retryable, two overlapping starts only ever
/// starting one timer — carried over onto the fake <see cref="TimeProvider"/> this coordinator now runs on. What
/// that file also proved (that a <c>DispatcherTimer</c> built off the UI thread never ticks at all) no longer
/// applies: the coordinator's timer is not bound to any particular thread any more.
/// </summary>
public class ScheduledResumeCoordinatorTimerTests
{
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

    private static ISessionHandle _Handle(List<string> sent)
    {
        var handle = Substitute.For<ISessionHandle>();
        handle.CanTakeAPrompt.Returns(true);
        handle.SendPromptAsync(Arg.Do<string>(sent.Add)).Returns(true);
        return handle;
    }

    [Fact]
    public async Task ATickThatThrows_IsSurvived_AndTheNextOneStillFires()
    {
        // A scheduler must never be the reason the cockpit falls over — but it must not fail in silence either.
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
    public async Task AStartThatFailed_CanBeStartedAgain()
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
    public async Task TwoStartsThatOverlap_OnlyOneOfThemStarts()
    {
        // The old guard looked at a field that is only set after the load, so two starts that overlap both got past
        // it. The loser's timer is then unreachable — Dispose cannot stop what it no longer holds — and keeps
        // ticking for the rest of the run.
        var clock = new ManualClock();
        var logger = new CountingLogger();

        using var coordinator = new ScheduledResumeCoordinator(new FailableStore(), toast: null, logger, TimeSpan.FromSeconds(30), clock);

        await Task.WhenAll(coordinator.StartAsync(), coordinator.StartAsync());

        Assert.Equal(1, logger.Started);
    }
}
