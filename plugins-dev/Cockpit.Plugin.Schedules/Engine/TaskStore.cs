using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules.Engine;

// The tasks, in the plugin's settings storage so they go along with a backup. Read fresh on every tick: they are
// edited elsewhere (the UI, MCP), and a tick working from yesterday's copy is worse than one a millisecond slower.
internal sealed class TaskStore(IPluginStorage storage)
{
    private const string Key = "tasks";

    public IReadOnlyList<ScheduledTask> Load() => StoreJson.Read<List<ScheduledTask>>(storage.Get<string>(Key)) ?? [];

    public void Save(IReadOnlyList<ScheduledTask> tasks) => storage.Set(Key, StoreJson.Write(tasks));
}
