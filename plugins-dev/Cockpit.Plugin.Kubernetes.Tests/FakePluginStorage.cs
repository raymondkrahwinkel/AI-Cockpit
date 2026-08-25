using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Kubernetes.Tests;

// An in-memory `IPluginStorage` for tests — stores values directly (no JSON round-trip), enough to drive the settings and secret layer.
internal sealed class FakePluginStorage : IPluginStorage
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    // AC-576 phase 3, AC 8: records which keys went through SetSecret rather than Set, so a test can prove a
    // credential (the Argo token) went through the secret layer and not the plain metadata path.
    public HashSet<string> SecretKeys { get; } = new(StringComparer.Ordinal);

    public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public void Set<T>(string key, T value) => _values[key] = value;

    public void SetSecret(string key, string value)
    {
        SecretKeys.Add(key);
        _values[key] = value;
    }

    public string? GetSecret(string key) => Get<string>(key);
}
