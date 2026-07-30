using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// <see cref="IPluginStorage"/> backed by an in-memory copy of the plugin's slice of <c>cockpit.json</c>,
/// seeded when the plugin loads. Values are JSON-serialized; <see cref="Set{T}"/> writes through the
/// supplied persist callback so the sync contract never blocks on file IO on the caller's thread.
/// <para>
/// Every access to <c>_values</c> goes through <see cref="_lock"/> — not for the dictionary mutation alone (that
/// much a single writer thread would already give you for free), but because <paramref name="persist"/> is
/// fire-and-forget: it reads its argument well after <see cref="Set{T}"/> has returned, on whatever thread the
/// host's file write happens to resume on. A plugin is no longer guaranteed to write from one thread only — a
/// background poll timer and the UI thread can both call <see cref="Set{T}"/> — so <see cref="Set{T}"/> hands
/// <paramref name="persist"/> a snapshot taken under the lock, never the live dictionary, or a write racing a
/// slow persist read would throw <see cref="InvalidOperationException"/> ("Collection was modified") from a task
/// nobody observes (AC-515).
/// </para>
/// </summary>
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

    /// <summary>
    /// Every key/value this plugin holds, as the raw JSON it was stored as. Host-side only — deliberately not on
    /// <see cref="IPluginStorage"/>, since a plugin has no business reading its own storage wholesale and even
    /// less another's. The host needs it to export a dashboard: it has to carry a widget's settings without
    /// knowing their shape.
    /// </summary>
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

    /// <summary>
    /// Stores a credential. The key is remembered as one — persisted, so the next start knows to decrypt it before
    /// handing it back rather than giving the plugin ciphertext, and so a backup that claims to carry no
    /// credentials empties it too. Then it is written like any other value.
    /// </summary>
    public void SetSecret(string key, string value)
    {
        _declareSecret?.Invoke(key);
        Set(key, value);
    }

    public string? GetSecret(string key) => Get<string>(key);
}
