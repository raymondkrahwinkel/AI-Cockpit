using Cockpit.Plugins.Abstractions.Health;

namespace Cockpit.Plugin.Workflows.Engine;

// AC-1466: the scheduler's heartbeat as a health section. FlowWatcher beats when a tick is through; longer than two
// ticks without one reads unhealthy, so a timer that stopped or a tick that hangs turns /healthz red.
internal sealed class SchedulerHealth(TimeProvider time, TimeSpan tick) : IPluginHealthSection
{
    private long _lastBeat = time.GetUtcNow().UtcTicks;

    public string Name => "workflows-scheduler";

    public void Beat() => Interlocked.Exchange(ref _lastBeat, time.GetUtcNow().UtcTicks);

    public PluginHealthReport Read()
    {
        var lastBeat = new DateTimeOffset(Interlocked.Read(ref _lastBeat), TimeSpan.Zero);
        var healthy = time.GetUtcNow() - lastBeat <= 2 * tick;
        return new PluginHealthReport(healthy, [new PluginHealthRow("Last tick", healthy ? PluginHealthStatus.Ok : PluginHealthStatus.Failed, lastBeat)]);
    }
}
