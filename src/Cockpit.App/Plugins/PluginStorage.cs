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

    public PluginStorage(IReadOnlyDictionary<string, string> seed, Action<IReadOnlyDictionary<string, string>> persist)
    {
        _values = new Dictionary<string, string>(seed);
        _persist = persist;
    }

    public T? Get<T>(string key) => _values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

    public void Set<T>(string key, T value)
    {
        _values[key] = JsonSerializer.Serialize(value);
        _persist(_values);
    }
}
