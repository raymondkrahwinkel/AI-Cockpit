using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Kind.Tests;

// An in-memory `IPluginStorage` for tests — stores values directly (no JSON round-trip). This plugin keeps no
// secrets: a kind cluster's kubeconfig is a file path on disk, so the secret half is here only to satisfy the interface.
internal sealed class FakePluginStorage : IPluginStorage
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public void Set<T>(string key, T value) => _values[key] = value;

    public void SetSecret(string key, string value) => _values[key] = value;

    public string? GetSecret(string key) => Get<string>(key);
}
