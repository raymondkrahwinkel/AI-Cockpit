using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>A minimal in-memory <see cref="IPluginStorage"/> for exercising settings round-trips without the host.</summary>
internal sealed class InMemoryPluginStorage : IPluginStorage
{
    private readonly Dictionary<string, object?> _values = [];

    public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public void Set<T>(string key, T value) => _values[key] = value;
}
