using System.Text.Json;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Core.Secrets;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Infrastructure.Plugins;

// AC-515: `IPluginStorage` over an in-memory copy of the plugin's `cockpit.json` slice; `Set{T}`
// persists asynchronously via callback. `_lock` guards `_values` because `persist` reads its
// snapshot argument later on another thread, so `Set{T}` must hand it a copy, not the live dictionary.
public sealed class PluginStorage : IPluginStorage
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _values;
    private readonly Action<IReadOnlyDictionary<string, string>> _persist;
    private readonly Action<string>? _declareSecret;

    public PluginStorage(
        IReadOnlyDictionary<string, string> seed,
        Action<IReadOnlyDictionary<string, string>> persist,
        Action<string>? declareSecret = null)
    {
        _values = new Dictionary<string, string>(seed);
        _persist = persist;
        _declareSecret = declareSecret;
    }

    // A loaded plugin's storage, seeded from its saved slice and written back through `store`. AC-1392: moved from App,
    // so a backend without one seeds a plugin the same way; the startup that calls this is synchronous by design.
    public static PluginStorage ForPlugin(DiscoveredPlugin discovered, IPluginRegistrationStore store, IPluginSecretFieldStore secretFieldStore)
    {
        var seed = store.LoadDataAsync(discovered.FolderId).GetAwaiter().GetResult();

        return new PluginStorage(
            seed,
            // AC-1343: fire-and-forget by contract (IPluginStorage.Set is void); the store drains this at exit.
            data => _ = store.SaveDataAsync(discovered.FolderId, data),
            // A key a plugin calls SetSecret on is remembered for the next start too: the name is what tells the
            // host to decrypt that field on the way in, and it would otherwise only be known while the plugin that
            // wrote it happened to be running.
            key =>
            {
                SecretKeyHolder.Shared.Declare([key]);
                // AC-1343: PluginSecretFieldStore.DisposeAsync drains this the same way, on the same
                // CockpitConfigFileAccess.FlushAsync as the registration store above.
                _ = secretFieldStore.DeclareAsync(discovered.FolderId, [key]);
            });
    }

    // Host-side only, not on `IPluginStorage`: a plugin has no business reading its own storage
    // wholesale, but the host needs the raw JSON to export a dashboard without knowing its shape.
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_values);
        }
    }

    public T? Get<T>(string key)
    {
        string? json;
        lock (_lock)
        {
            _values.TryGetValue(key, out json);
        }

        return json is null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public void Set<T>(string key, T value)
    {
        Dictionary<string, string> snapshot;
        lock (_lock)
        {
            _values[key] = JsonSerializer.Serialize(value);
            snapshot = new Dictionary<string, string>(_values);
        }

        _persist(snapshot);
    }

    public void Remove(string key)
    {
        Dictionary<string, string>? snapshot = null;
        lock (_lock)
        {
            if (_values.Remove(key))
            {
                snapshot = new Dictionary<string, string>(_values);
            }
        }

        if (snapshot is not null)
        {
            _persist(snapshot);
        }
    }

    // Stores a credential. The key is remembered as one — persisted, so the next start knows to decrypt it before
    // handing it back rather than giving the plugin ciphertext, and so a backup that claims to carry no
    // credentials empties it too. Then it is written like any other value.
    public void SetSecret(string key, string value)
    {
        _declareSecret?.Invoke(key);
        Set(key, value);
    }

    public string? GetSecret(string key) => Get<string>(key);
}
