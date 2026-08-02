using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// `IPluginStorage` backed by an in-memory copy of the plugin's slice of `cockpit.json`,
// seeded when the plugin loads. Values are JSON-serialized; `Set{T}` writes through the
// supplied persist callback so the sync contract never blocks on file IO on the caller's thread.
//
// Every access to `_values` goes through `_lock` — not for the dictionary mutation alone (that
// much a single writer thread would already give you for free), but because `persist` is
// fire-and-forget: it reads its argument well after `Set{T}` has returned, on whatever thread the
// host's file write happens to resume on. A plugin is no longer guaranteed to write from one thread only — a
// background poll timer and the UI thread can both call `Set{T}` — so `Set{T}` hands
// `persist` a snapshot taken under the lock, never the live dictionary, or a write racing a
// slow persist read would throw `InvalidOperationException` ("Collection was modified") from a task
// nobody observes (AC-515).
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

    // Every key/value this plugin holds, as the raw JSON it was stored as. Host-side only — deliberately not on
    // `IPluginStorage`, since a plugin has no business reading its own storage wholesale and even
    // less another's. The host needs it to export a dashboard: it has to carry a widget's settings without
    // knowing their shape.
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
