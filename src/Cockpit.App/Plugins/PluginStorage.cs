using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// <see cref="IPluginStorage"/> backed by an in-memory copy of the plugin's slice of <c>cockpit.json</c>,
/// seeded when the plugin loads. Values are JSON-serialized; <see cref="Set{T}"/> writes through the
/// supplied persist callback so the sync contract never blocks on file IO on the caller's thread.
/// </summary>
public sealed class PluginStorage : IPluginStorage
{
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
    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(_values);

    public T? Get<T>(string key) => _values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

    public void Set<T>(string key, T value)
    {
        _values[key] = JsonSerializer.Serialize(value);
        _persist(_values);
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
