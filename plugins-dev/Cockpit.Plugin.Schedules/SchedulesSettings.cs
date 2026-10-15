using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules;

// The plugin's own settings, read live so a change takes effect on the next run without a restart.
internal sealed class SchedulesSettings(IPluginStorage storage)
{
    private const string MaxConcurrentRunsKey = "max-concurrent-runs";
    private const int DefaultMaxConcurrentRuns = 2;

    public int MaxConcurrentRuns => Math.Max(1, storage.Get<int?>(MaxConcurrentRunsKey) ?? DefaultMaxConcurrentRuns);
}
