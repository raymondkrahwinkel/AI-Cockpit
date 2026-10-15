using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules.Tests;

// Settings storage and cache in one dictionary; kept between two engines, it is what survives a restart.
internal sealed class InMemoryPluginStorage : IPluginStorage, IPluginCache
{
    private readonly Dictionary<string, string> _values = [];

    public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? JsonSerializer.Deserialize<T>(value) : default;

    public void Set<T>(string key, T value) => _values[key] = JsonSerializer.Serialize(value);

    public void Remove(string key) => _values.Remove(key);
}
