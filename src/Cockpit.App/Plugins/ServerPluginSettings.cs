using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.Plugins;

// AC-1571: a plugin's own settings view, opened for a server. A fresh copy of its UI part is seeded with the server's
// storage slice (each set credential reading "(set on the server)") and reaches its backend part there; Save sends
// what the view wrote, then the plugin's saved callbacks run, so a reconnect sees the new values.
internal sealed class ServerPluginSettings
{
    private readonly IPluginChannelRelay _relay;
    private readonly string _folderId;
    private readonly PluginStorage _storage;
    private readonly ServerPluginUiHost _host;

    // `_sync` keeps the view's writes, a reread's takeover and Save's diff apart: a reread runs on a pool thread while
    // the view commits on the UI thread. Rereads run one at a time, so an older answer never lands after a newer one.
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _rereading = new(1, 1);
    private readonly Dictionary<string, string> _seed;
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

    private ServerPluginSettings(
        IPluginChannelRelay relay,
        string folderId,
        IReadOnlyDictionary<string, string> seed,
        ICockpitUiHost local,
        Func<Task<IReadOnlyList<PluginProfileInfo>>> profiles)
    {
        _relay = relay;
        _folderId = folderId;
        _seed = new Dictionary<string, string>(seed, StringComparer.Ordinal);
        _storage = new PluginStorage(seed, _ => { });
        _host = new ServerPluginUiHost(
            local,
            new ViewStorage(this),
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
    // Only keys the view itself wrote go out, and only where they differ from what the server last said.
    public async Task<string?> PersistAsync()
    {
        Dictionary<string, string> changed;
        List<string> removed;
        string[] secrets;
        lock (_sync)
        {
            var now = _storage.Snapshot();
            changed = _written.Where(key => now.TryGetValue(key, out var value) && !(_seed.TryGetValue(key, out var was) && _SameJson(was, value)))
                .ToDictionary(key => key, key => now[key], StringComparer.Ordinal);
            removed = [.. _written.Where(key => !now.ContainsKey(key) && _seed.ContainsKey(key))];
            secrets = [.. _secrets.Where(changed.ContainsKey)];
        }

        try
        {
            if (changed.Count > 0 || removed.Count > 0)
            {
                await _relay.SaveStorageAsync(_folderId, changed, removed, secrets);
            }

            lock (_sync)
            {
                foreach (var (key, value) in changed)
                {
                    _seed[key] = value;
                }

                foreach (var key in removed)
                {
                    _seed.Remove(key);
                }

                // A key the view wrote again while this Save was out stays written, for the next one.
                var now = _storage.Snapshot();
                _written.RemoveWhere(key => now.TryGetValue(key, out var value) ? _seed.TryGetValue(key, out var was) && _SameJson(was, value) : !_seed.ContainsKey(key));
            }

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
    // every key the view has not written itself. A failed read leaves the copy as it was, the action having run.
    private async Task _RefreshAsync()
    {
        await _rereading.WaitAsync().ConfigureAwait(false);
        try
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

            lock (_sync)
            {
                foreach (var key in server.Keys.Union(_seed.Keys).Where(key => !_written.Contains(key)).ToList())
                {
                    if (server.TryGetValue(key, out var json))
                    {
                        _storage.Set(key, _Parse(json));
                        _seed[key] = json;
                    }
                    else
                    {
                        _storage.Remove(key);
                        _seed.Remove(key);
                    }
                }
            }
        }
        finally
        {
            _rereading.Release();
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
        catch (JsonException)
        {
            return json;
        }
    }

    // The storage the view sees: every write it makes is marked as its own, under the same lock a reread takes.
    private sealed class ViewStorage(ServerPluginSettings owner) : IPluginStorage
    {
        public T? Get<T>(string key) => owner._storage.Get<T>(key);

        public string? GetSecret(string key) => owner._storage.GetSecret(key);

        public void Set<T>(string key, T value) => _Write(key, () => owner._storage.Set(key, value));

        public void Remove(string key) => _Write(key, () => owner._storage.Remove(key));

        public void SetSecret(string key, string value) => _Write(key, () =>
        {
            owner._secrets.Add(key);
            owner._storage.SetSecret(key, value);
        });

        private void _Write(string key, Action write)
        {
            lock (owner._sync)
            {
                owner._written.Add(key);
                write();
            }
        }
    }

    private sealed class RefreshingChannel(IPluginUiChannel inner, Func<Task> refresh) : IPluginUiChannel
    {
        public async Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default)
        {
            var answer = await inner.InvokeAsync(action, payload, cancellationToken).ConfigureAwait(false);
            return refresh is null ? answer : answer;
        }

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => inner.Subscribe(name, handler);
    }
}
