using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Kubernetes.Tests;

/// <summary>An in-memory <see cref="IPluginStorage"/> for tests — stores values directly (no JSON round-trip), enough to drive the settings and secret layer.</summary>
internal sealed class FakePluginStorage : IPluginStorage
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public void Set<T>(string key, T value) => _values[key] = value;
}
