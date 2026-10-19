using System.Collections.Concurrent;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Projects;

// AC-1570: the baseline is laid at start and the changed flag stays until someone acts on it.
public sealed class DepotSyncWatcherTests : IDisposable
{
    private readonly _Clock _time = new();
    private readonly ISharedProjectSource _source = Substitute.For<ISharedProjectSource>();
    private readonly DepotSyncWatcher _watcher;
    private readonly BlockingCollection<bool> _reports = [];
    private string _checksum = "a";
    private bool _bound = true;

    public DepotSyncWatcherTests()
    {
        _source.PrepareBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SharedProjectBindingResult.Success(new SharedProjectBinding("p") { Checksum = _checksum })));
        _watcher = new DepotSyncWatcher(null, _time)
        {
            BoundProjects = () => Task.FromResult<IReadOnlyList<DepotBoundProject>>(_bound ? [new DepotBoundProject("p", _source, "shared")] : []),
            OnChecked = (_, changed, _) =>
            {
                _reports.Add(changed);
                return Task.CompletedTask;
            },
        };
    }

    [Fact]
    public void ChangeWithinFirstInterval_IsSeenAtTheFirstTickAfterIt()
    {
        _watcher.Start();
        _time.Advance(TimeSpan.Zero);
        Assert.False(_Next());

        _checksum = "b";
        _time.Advance(TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(14));

        Assert.True(_Next());
    }

    [Fact]
    public async Task ChangedFlag_StaysThroughQuietTicks_AndGoesOnSyncNow()
    {
        _watcher.Start();
        _time.Advance(TimeSpan.Zero);
        _Next();
        _checksum = "b";
        _time.Advance(TimeSpan.FromMinutes(15));
        _Next();

        _time.Advance(TimeSpan.FromMinutes(15));
        var afterQuietTick = _Next();
        await _watcher.SyncNowAsync("p");
        var afterSyncNow = _Next();

        Assert.True(afterQuietTick);
        Assert.False(afterSyncNow);
    }

    [Fact]
    public void UnboundProject_LosesItsFlagAndItsBaseline()
    {
        _watcher.Start();
        _time.Advance(TimeSpan.Zero);
        _Next();
        _checksum = "b";
        _time.Advance(TimeSpan.FromMinutes(15));
        _Next();

        _bound = false;
        _time.Advance(TimeSpan.FromMinutes(15));
        var afterUnbind = _Next();
        _bound = true;
        _time.Advance(TimeSpan.FromMinutes(15));

        Assert.False(afterUnbind);
        Assert.False(_Next());
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _reports.Dispose();
    }

    private bool _Next() =>
        _reports.TryTake(out var changed, TimeSpan.FromSeconds(10)) ? changed : throw new TimeoutException("The watcher reported no check.");

    // Timers fire, and repeat, only when the clock is advanced past them.
    private sealed class _Clock : TimeProvider
    {
        private readonly List<_Alarm> _alarms = [];
        private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var alarm = new _Alarm(_now + dueTime, period, callback, state);
            _alarms.Add(alarm);
            return alarm;
        }

        public void Advance(TimeSpan by)
        {
            var target = _now + by;
            while (_alarms.Where(alarm => !alarm.Disposed && alarm.Due <= target).MinBy(alarm => alarm.Due) is { } next)
            {
                _now = next.Due > _now ? next.Due : _now;
                next.Due += next.Period;
                next.Callback(next.State);
            }

            _now = target;
        }
    }

    private sealed class _Alarm(DateTimeOffset due, TimeSpan period, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; set; } = due;

        public TimeSpan Period { get; } = period;

        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
