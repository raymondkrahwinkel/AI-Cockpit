using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules.Engine;

// Per task, the last moment the engine has dealt with: a slot at or before it never fires again. In the plugin cache,
// which the host writes to disk synchronously, so the mark is on disk before the action starts; settings storage
// saves in the background, and a crash in between would replay the slot (plan §4 said Storage; this is why not).
internal sealed class MarkStore(IPluginCache cache)
{
    private const string Key = "marks";

    public TaskMark? For(string taskId) => _Load().GetValueOrDefault(taskId);

    public void Set(string taskId, TaskMark mark)
    {
        var marks = _Load();
        marks[taskId] = mark;
        cache.Set(Key, StoreJson.Write(marks));
    }

    // Drops the marks of tasks that are gone, disabled or unreadable, so they count from scratch when they come back.
    public void Retain(IReadOnlySet<string> taskIds)
    {
        var marks = _Load();
        if (marks.Keys.Count(taskIds.Contains) == marks.Count)
        {
            return;
        }

        cache.Set(Key, StoreJson.Write(marks.Where(mark => taskIds.Contains(mark.Key)).ToDictionary()));
    }

    private Dictionary<string, TaskMark> _Load() =>
        StoreJson.Read<Dictionary<string, TaskMark>>(cache.Get<string>(Key)) ?? [];
}
