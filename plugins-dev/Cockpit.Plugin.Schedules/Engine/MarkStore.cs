using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules.Engine;

// Per task, the last moment dealt with; no slot at or before it fires again. In the cache: written synchronously, and
// outside the backup on purpose, so a restore cannot put back an old mark and replay slots. A new machine or an
// unreadable file loses the marks; that costs at most a skipped slot, never a double run (a task re-arms at "now").
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
