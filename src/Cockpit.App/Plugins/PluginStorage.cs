using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

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
