using Cockpit.Plugins.Abstractions.Health;

namespace Cockpit.Plugin.Schedules.Engine;

// The engine for /healthz, as Workflows' scheduler section (AC-1466): unhealthy past two ticks without one, and while
// an enabled task has a schedule or zone this machine cannot read, because such a task never fires.
internal sealed class EngineHealth(TimeProvider time, TimeSpan tick) : IPluginHealthSection
{
    private long _lastBeat = time.GetUtcNow().UtcTicks;
    private int _unusable;

    public string Name => "schedules-engine";

    public void Beat(int unusableTasks)
    {
        Interlocked.Exchange(ref _unusable, unusableTasks);
        Interlocked.Exchange(ref _lastBeat, time.GetUtcNow().UtcTicks);
    }

    public PluginHealthReport Read()
    {
        var lastBeat = new DateTimeOffset(Interlocked.Read(ref _lastBeat), TimeSpan.Zero);
        var ticking = time.GetUtcNow() - lastBeat <= 2 * tick;
        var unusable = Volatile.Read(ref _unusable);
        List<PluginHealthRow> rows = [new("Last tick", ticking ? PluginHealthStatus.Ok : PluginHealthStatus.Failed, lastBeat)];
        if (unusable > 0)
        {
            rows.Add(new($"{unusable} enabled task(s) with an unreadable schedule or time zone", PluginHealthStatus.Failed));
        }

        return new PluginHealthReport(ticking && unusable == 0, rows);
    }
}
