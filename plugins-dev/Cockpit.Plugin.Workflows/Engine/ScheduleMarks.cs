using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

// The last scheduled slot each trigger has already dealt with (#AC-1359) — Fire, Late and Missed all count. Set
// before a flow runs, not after: a crash mid-run must not replay the same slot on the next start, which is what
// "at most once" means here. Kept next to `runs` in the same per-plugin cache.
internal sealed class ScheduleMarks(IPluginCache cache)
{
    private const string Key = "schedule-marks";

    public DateTimeOffset? For(string workflowId, string triggerId) =>
        _Load().GetValueOrDefault(_Key(workflowId, triggerId));

    public void Set(string workflowId, string triggerId, DateTimeOffset slot)
    {
        var marks = _Load();
        marks[_Key(workflowId, triggerId)] = slot;
        cache.Set(Key, marks);
    }

    private Dictionary<string, DateTimeOffset> _Load() =>
        cache.Get<Dictionary<string, DateTimeOffset>>(Key) ?? [];

    private static string _Key(string workflowId, string triggerId) => $"{workflowId}:{triggerId}";
}
