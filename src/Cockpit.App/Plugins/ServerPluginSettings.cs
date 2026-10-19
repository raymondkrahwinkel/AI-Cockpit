using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Profiles;
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

    private ServerPluginSettings(
        IPluginChannelRelay relay,
        string folderId,
        IReadOnlyDictionary<string, string> seed,
        ICockpitUiHost local,
        Func<Task<IReadOnlyList<PluginProfileInfo>>> profiles)
    {
        _relay = relay;
        _folderId = folderId;
        _seed = seed;
        _storage = new PluginStorage(seed, _ => { }, key => _secrets.Add(key));
        _host = new ServerPluginUiHost(
            local,
            _storage,
            new RefreshingChannel(new RemotePluginUiChannel(relay, folderId), _RefreshAsync),
            async (name, cancellationToken) => Enum.TryParse<PluginMcpAuthState>(
                await relay.GetMcpServerAuthStateAsync(folderId, name, cancellationToken).ConfigureAwait(false), ignoreCase: true, out var state)
                ? state
                : PluginMcpAuthState.Unknown,
            profiles);
    }

    public Func<Control> CreateView => _host.CreateSettingsView ?? throw new InvalidOperationException("The plugin registered no settings view.");

    // Null when the copy registers no settings view; a UI part that throws in InitializeUi throws here.
    public static async Task<ServerPluginSettings?> OpenAsync(
        Type uiType,
        ICockpitUiHost local,
        IPluginChannelRelay relay,
        string folderId,
        Func<Task<IReadOnlyList<PluginProfileInfo>>> profiles)
    {
        var settings = new ServerPluginSettings(relay, folderId, await relay.GetStorageAsync(folderId), local, profiles);
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

    // A channel action can have its backend part write storage there (Depot's connection list); the copy takes over
    // every key the view did not change itself. A failed read leaves the copy as it was, the action having run.
    private async Task _RefreshAsync()
    {
        IReadOnlyDictionary<string, string> server;
        try
        {
            server = await _relay.GetStorageAsync(_folderId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return;
        }

        var mine = _storage.Snapshot();
        var seed = new Dictionary<string, string>(_seed, StringComparer.Ordinal);
        foreach (var key in server.Keys.Union(_seed.Keys))
        {
            var untouched = _seed.TryGetValue(key, out var was)
                ? mine.TryGetValue(key, out var current) && _SameJson(was, current)
                : !mine.ContainsKey(key);
            if (!untouched)
            {
                continue;
            }

            if (server.TryGetValue(key, out var json))
            {
                _storage.Set(key, _Parse(json));
                seed[key] = json;
            }
            else
            {
                _storage.Remove(key);
                seed.Remove(key);
            }
        }

        _seed = seed;
    }

    private static bool _SameJson(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal) || JsonNode.DeepEquals(_Parse(left), _Parse(right));

    private static JsonNode? _Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private sealed class RefreshingChannel(IPluginUiChannel inner, Func<Task> refresh) : IPluginUiChannel
    {
        public async Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default)
        {
            var answer = await inner.InvokeAsync(action, payload, cancellationToken).ConfigureAwait(false);
            await refresh().ConfigureAwait(false);
            return answer;
        }

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => inner.Subscribe(name, handler);
    }
}
