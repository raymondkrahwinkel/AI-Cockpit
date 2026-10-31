using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.Shared;

internal sealed class ProviderChildLedger
{
    private readonly Dictionary<string, PluginBackgroundTask> _known = new();
    private readonly Dictionary<string, long> _order = new();
    private long _sequence;
    public PluginBackgroundTask? Find(string id) => _known.GetValueOrDefault(id);
    public PluginBackgroundTask? GetValueOrDefault(string id) => Find(id);
    public bool TryGetValue(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PluginBackgroundTask? child) => _known.TryGetValue(id, out child);
    public PluginBackgroundTask this[string id] { set => Remember(value); }
    public void Clear() { _known.Clear(); _order.Clear(); }
    public void Remember(PluginBackgroundTask child)
    {
        if (!_known.TryGetValue(child.TaskId, out var previous) || previous.RunId != child.RunId || previous.SourceRevision != child.SourceRevision || previous.State != child.State)
        {
            _order[child.TaskId] = ++_sequence;
        }
        _known[child.TaskId] = child;
    }
    public IReadOnlyList<PluginBackgroundTask> Snapshot(IReadOnlyList<PluginBackgroundTask> active)
    {
        HashSet<string> ids = active.Select(child => child.TaskId).ToHashSet();
        foreach (var old in _known.Values.ToArray())
        {
            if (!ids.Contains(old.TaskId) && old.State == "running")
            {
                Remember(old with { State = "unknown" });
            }
        }
        foreach (var child in active.Where(child => child.Kind == PluginBackgroundTaskKind.SubAgent))
        {
            Remember(child);
        }
        while (_known.Count > 128)
        {
            PluginBackgroundTask? oldest = _known.Values.Where(child => !ids.Contains(child.TaskId)).MinBy(child => _order[child.TaskId]);
            if (oldest is null)
            {
                break;
            }
            _known.Remove(oldest.TaskId);
            _order.Remove(oldest.TaskId);
        }
        return [.. _known.Values];
    }
}
