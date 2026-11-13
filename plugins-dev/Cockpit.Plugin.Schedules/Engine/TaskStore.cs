using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules.Engine;

// The tasks, in the plugin's settings storage so they go along with a backup. Read fresh on every tick: they are
// edited elsewhere (the UI, MCP). Null when a stored list cannot be read, which health reports rather than "no tasks".
internal sealed class TaskStore(IPluginStorage storage)
{
    private const string Key = "tasks";

    public IReadOnlyList<ScheduledTask>? Load() =>
        storage.Get<string>(Key) is { Length: > 0 } json ? StoreJson.Read<List<ScheduledTask>>(json) : [];

    // Raised after every save, so the engine arms a new or edited schedule at once instead of at its next tick: a slot
    // that falls between the edit and that tick would otherwise be missed for good.
    public event Action? Saved;

    public void Save(IReadOnlyList<ScheduledTask> tasks)
    {
        storage.Set(Key, StoreJson.Write(tasks));
        Saved?.Invoke();
    }
}
