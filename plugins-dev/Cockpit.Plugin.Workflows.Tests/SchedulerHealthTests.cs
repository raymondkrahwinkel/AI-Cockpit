using NSubstitute;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Tests;

// AC-1466 criterion 1: the scheduler's section holds for two ticks without one, turns unhealthy past that, and
// holds again once the watcher's clock ticks.
public class SchedulerHealthTests
{
    [Fact]
    public void TheSchedulerSection_TurnsUnhealthyPastTwoMissedTicks_AndHealthyOnTheNextTick()
    {
        var storage = new InMemoryPluginStorage();
        var clock = new HeldClock();
        using var watcher = new FlowWatcher(new WorkflowStore(storage), new RunStore(storage), new ScheduleMarks(storage), Substitute.For<ICockpitHost>(), () => TimeSpan.Zero, clock);

        clock.Advance(TimeSpan.FromSeconds(60));
        var twoMissed = watcher.Health.Read().Healthy;
        clock.Advance(TimeSpan.FromSeconds(1));
        var pastTwo = watcher.Health.Read().Healthy;
        clock.Tick();
        var ticked = watcher.Health.Read().Healthy;

        Assert.Equal((true, false, true), (twoMissed, pastTwo, ticked));
    }

    // A clock that moves and ticks only when the test says so.
    private sealed class HeldClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private TimerCallback? _tick;
        private object? _state;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

        public void Tick() => _tick?.Invoke(_state);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _tick = callback;
            _state = state;
            return new HeldTimer();
        }

        private sealed class HeldTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
