using Cockpit.Core.Layout;
using Cockpit.Plugins.Abstractions.CompanionTools;

namespace Cockpit.App.ViewModels;

internal sealed class CompanionCustomizationModel
{
    internal const string AssistantId = "cockpit.assistant";
    private static readonly string[] _Defaults = [AssistantId, "cockpit.screenshot", "cockpit.status", "cockpit.notifications"];
    private readonly List<string> _order = [];
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _titles = [];
    private readonly Dictionary<string, string> _plugins = [];

    public CompanionCustomizationModel(CompanionStripLayout? saved = null) => Restore(saved);

    public void Restore(CompanionStripLayout? saved)
    {
        _order.Clear();
        _order.Add(AssistantId);
        _order.AddRange((saved?.Order ?? _Defaults).Where(id => id != AssistantId).Distinct());
        _hidden.Clear();
        _hidden.UnionWith(saved?.HiddenIds ?? []);
        _seen.Clear();
        _seen.UnionWith(saved?.SeenIds ?? _Defaults);
        _titles.Clear();
        foreach (var (id, title) in saved?.Titles ?? new Dictionary<string, string>())
        {
            _titles[id] = title;
        }
        _plugins.Clear();
        foreach (var (id, plugin) in saved?.PluginIds ?? new Dictionary<string, string>())
        {
            _plugins[id] = plugin;
        }
        _hidden.Remove(AssistantId);
    }

    public event Action? Changed;
    public event Action? Refreshed;
    private string? _notice;
    public string? Notice
    {
        get => _notice;
        set
        {
            _notice = value;
            Refreshed?.Invoke();
        }
    }
    public IReadOnlyList<CompanionToolRegistration> Tools { get; private set; } = [];
    public IEnumerable<string> OnStrip => _order.Where(id => !_hidden.Contains(id) && Tools.Any(tool => tool.Id == id));
    public IEnumerable<string> Available => Tools.Select(tool => tool.Id).Where(_hidden.Contains);
    public IEnumerable<string> Unavailable => _order.Where(id => id != AssistantId && Tools.All(tool => tool.Id != id));
    public string Title(string id) => _titles.GetValueOrDefault(id, id);
    public string? PluginId(string id) => _plugins.GetValueOrDefault(id);
    public CompanionStripLayout Layout => new()
    {
        Order = [.. _order], HiddenIds = [.. _hidden], SeenIds = [.. _seen],
        Titles = new Dictionary<string, string>(_titles), PluginIds = new Dictionary<string, string>(_plugins),
    };

    public IReadOnlyList<string> Merge(IReadOnlyList<CompanionToolRegistration> tools, Func<string, string?> pluginId)
    {
        var removed = Tools.Where(old => tools.All(tool => tool.Id != old.Id)).Select(tool => tool.Title).ToList();
        Tools = tools;
        foreach (var tool in tools)
        {
            if (_seen.Add(tool.Id))
            {
                _hidden.Add(tool.Id);
            }
            if (!_order.Contains(tool.Id))
            {
                _order.Add(tool.Id);
            }
            _titles[tool.Id] = tool.Title;
            if (pluginId(tool.Id) is { } owner)
            {
                _plugins[tool.Id] = owner;
            }
        }
        _hidden.Remove(AssistantId);
        Refreshed?.Invoke();
        return removed;
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (id == AssistantId || !_order.Contains(id))
        {
            return;
        }
        if (enabled)
        {
            _hidden.Remove(id);
        }
        else
        {
            _hidden.Add(id);
        }
        Changed?.Invoke();
    }

    public void Move(string id, int direction)
    {
        var visible = OnStrip.ToList();
        var index = visible.IndexOf(id);
        var target = index + direction;
        if (index <= 0 || target <= 0 || target >= visible.Count)
        {
            return;
        }
        var from = _order.IndexOf(id);
        var to = _order.IndexOf(visible[target]);
        (_order[from], _order[to]) = (_order[to], _order[from]);
        Changed?.Invoke();
    }

    public void Forget(string id)
    {
        if (!Unavailable.Contains(id))
        {
            return;
        }
        _order.Remove(id);
        _hidden.Remove(id);
        _seen.Remove(id);
        _titles.Remove(id);
        _plugins.Remove(id);
        Changed?.Invoke();
    }
}
