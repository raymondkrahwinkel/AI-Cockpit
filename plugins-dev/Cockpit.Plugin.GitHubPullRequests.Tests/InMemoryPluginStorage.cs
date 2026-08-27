using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// An in-memory `IPluginStorage` that keeps the host's JSON round-trip semantics.
internal sealed class InMemoryPluginStorage : IPluginStorage
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public void SeedRaw(string key, string rawJson) => _store[key] = rawJson;

    public string Raw(string key) => _store[key];

    public T? Get<T>(string key) => _store.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

    public void Set<T>(string key, T value) => _store[key] = JsonSerializer.Serialize(value);
}
