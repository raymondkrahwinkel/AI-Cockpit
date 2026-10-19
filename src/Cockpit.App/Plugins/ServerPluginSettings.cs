using System.Text.Json.Nodes;
using Avalonia.Controls;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.Plugins;

// AC-1571: a plugin's own settings view, opened for a server. A fresh copy of its UI part is seeded with the server's
// storage slice (each set credential reading "(set on the server)") and reaches its backend part there; Save sends
// what the view changed, then the plugin's saved callbacks run, so a reconnect sees the new values.
internal sealed class ServerPluginSettings
{
    private readonly IPluginChannelRelay _relay;
    private readonly string _folderId;
    private readonly PluginStorage _storage;
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly ServerPluginUiHost _host;
    private IReadOnlyDictionary<string, string> _seed;

    private ServerPluginSettings(IPluginChannelRelay relay, string folderId, IReadOnlyDictionary<string, string> seed, ICockpitUiHost local)
    {
        _relay = relay;
        _folderId = folderId;
        _seed = seed;
        _storage = new PluginStorage(seed, _ => { }, key => _secrets.Add(key));
        _host = new ServerPluginUiHost(local, _storage, new RemotePluginUiChannel(relay, folderId));
    }

    public Func<Control> CreateView => _host.CreateSettingsView ?? throw new InvalidOperationException("The plugin registered no settings view.");

    // Null when the copy registers no settings view; a UI part that throws in InitializeUi throws here.
    public static async Task<ServerPluginSettings?> OpenAsync(Type uiType, ICockpitUiHost local, IPluginChannelRelay relay, string folderId)
    {
        var settings = new ServerPluginSettings(relay, folderId, await relay.GetStorageAsync(folderId), local);
        if (Activator.CreateInstance(uiType) is not ICockpitPluginUi ui)
        {
            return null;
        }

        ui.InitializeUi(settings._host);
        return settings._host.HasSettings ? settings : null;
    }

    // Runs after the view's own commit wrote into the copy's storage; the reason when the server refused or failed.
    public async Task<string?> PersistAsync()
    {
        var now = _storage.Snapshot();
        var changed = now.Where(entry => !_seed.TryGetValue(entry.Key, out var was) || !_SameJson(was, entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var removed = _seed.Keys.Where(key => !now.ContainsKey(key)).ToList();
        try
        {
            if (changed.Count > 0 || removed.Count > 0)
            {
                await _relay.SaveStorageAsync(_folderId, changed, removed, [.. _secrets.Where(changed.ContainsKey)]);
            }

            // The next Save in the same window sends only what changed after this one.
            _seed = now;
            foreach (var saved in _host.SettingsSaved)
            {
                saved();
            }

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"The server did not save it: {exception.Message}";
        }
    }

    private static bool _SameJson(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal) || JsonNode.DeepEquals(_Parse(left), _Parse(right));

    private static JsonNode? _Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }
}
