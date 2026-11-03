using System.Collections.Concurrent;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

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
    public async Task BindingWithReadChecksum_ReportsFirstLaterRemoteChange()
    {
        var settings = ProjectSettings.Empty;
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(settings));
        store.SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            settings = call.Arg<ProjectSettings>();
            return Task.CompletedTask;
        });
        var sources = new SharedProjectSourceRegistry();
        sources.Register(_source);
        var catalog = new ProjectCatalog(store, new ProjectOwnershipRegistry(), sources, depotSync: _watcher);
        var boundProjects = _watcher.BoundProjects;
        Assert.NotNull(boundProjects);
        var snapshotTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher.BoundProjects = async () =>
        {
            var snapshot = await boundProjects();
            snapshotTaken.TrySetResult();
            await releaseSnapshot.Task;
            return snapshot;
        };
        _watcher.OnChecked = (_, changed, _) =>
        {
            _reports.Add(changed);
            return Task.CompletedTask;
        };
        var project = new Project("p", "Bound project")
        {
            Resources = [new ProjectResource("fake:shared", ProjectResourceRole.Memory)],
            SharedSourceName = _source.SourceName,
        };

        var stalePass = _watcher.RunOnceAsync();
        await snapshotTaken.Task;
        await catalog.AddBoundProjectAsync(project, "a");
        releaseSnapshot.SetResult();
        await stalePass;
        _checksum = "b";

        await _watcher.CheckNowAsync(project.Id);

        Assert.True(_Next());
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

        public Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectListResult.Success([new SharedProject("fake:shared", "p")]));

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
