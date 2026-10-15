using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules.Engine;

// Per task, the last moment the engine has dealt with: a slot at or before it never fires again. Written before an
// action starts, in settings storage rather than the cache, so a restart mid-run or a clock that jumps back cannot
// replay a slot (plan §4).
internal sealed class MarkStore(IPluginStorage storage)
{
    private const string Key = "marks";

    public DateTimeOffset? For(string taskId) => _Load().TryGetValue(taskId, out var mark) ? mark : null;

    public void Set(string taskId, DateTimeOffset mark)
    {
        var marks = _Load();
        marks[taskId] = mark;
        storage.Set(Key, StoreJson.Write(marks));
    }

    private Dictionary<string, DateTimeOffset> _Load() =>
        StoreJson.Read<Dictionary<string, DateTimeOffset>>(storage.Get<string>(Key)) ?? [];
}
