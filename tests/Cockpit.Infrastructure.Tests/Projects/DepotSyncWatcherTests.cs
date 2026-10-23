using System.Collections.Concurrent;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Tests.Projects;

// AC-1570: the baseline is laid at start and the changed flag stays until someone acts on it.
public sealed class DepotSyncWatcherTests : IDisposable
{
    private readonly _Clock _time = new();
    private readonly _Source _source = new();
    private readonly DepotSyncWatcher _watcher;
    private readonly BlockingCollection<bool> _reports = [];
    private string _checksum = "a";
    private bool _bound = true;

    public DepotSyncWatcherTests()
    {
        _source.Answer = () => SharedProjectBindingResult.Success(new SharedProjectBinding("p") { Checksum = _checksum });
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
    public async Task SourceChange_ChecksTheBoundProjectOnce_AndSetsTheFlagWithoutAdvancingTheClock()
    {
        await _watcher.RunOnceAsync();
        Assert.False(_Next());
        _source.PrepareCalls = 0;
        var before = _time.GetUtcNow();
        _checksum = "b";

        _source.RaiseChanged("shared");

        Assert.True(_Next());
        Assert.Equal(before, _time.GetUtcNow());
        Assert.Equal(1, _source.PrepareCalls);
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

    [Fact]
    public async Task ACheckThatCannotAnswer_IsToldOnce_AlsoWhenItFirstFailedBeforeAnyoneListened_AndAfterRecovering()
    {
        var told = new List<(string ProjectId, string Reason)>();

        // The first pass runs at start, before anyone listens: what it found is still said by the next check.
        _source.Answer = () => SharedProjectBindingResult.Failed("Not signed in.");
        await _watcher.RunOnceAsync();
        _watcher.OnFailing = (projectId, reason) => told.Add((projectId, reason));
        await _watcher.RunOnceAsync();
        await _watcher.RunOnceAsync();
        await _watcher.RunOnceAsync();

        _source.Answer = () => SharedProjectBindingResult.Success(new SharedProjectBinding("p") { Checksum = "a" });
        await _watcher.RunOnceAsync();
        _source.Answer = () => SharedProjectBindingResult.Failed("Not signed in.");
        await _watcher.RunOnceAsync();

        Assert.Equal([("p", "Not signed in.")], told);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _reports.Dispose();
    }

    private bool _Next() =>
        _reports.TryTake(out var changed, TimeSpan.FromSeconds(10)) ? changed : throw new TimeoutException("The watcher reported no check.");

    private sealed class _Source : ISharedProjectSource
    {
        public Func<SharedProjectBindingResult> Answer { get; set; } = () => SharedProjectBindingResult.Failed("No answer.");

        public int PrepareCalls { get; set; }

        public event Action<string?>? Changed;

        public string Key => "fake";

        public string SourceName => "Fake";

        public bool CanPublish => false;

        public void RaiseChanged(string? id) => Changed?.Invoke(id);

        public Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            return Task.FromResult(Answer());
        }

        public Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SharedProjectWriteBackResult> WriteBackAsync(
            string id, SharedProjectDefinitionEdit edit, string baseChecksum, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SharedProjectPublishResult> PublishAsync(
            string targetId, SharedProjectPublishDefinition definition, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

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
