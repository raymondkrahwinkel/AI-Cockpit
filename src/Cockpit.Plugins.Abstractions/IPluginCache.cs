using System.Collections.Concurrent;
using System.Text.Json;

namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// Per-plugin key/value cache for what the plugin can rebuild by itself: fetched data, run history, a snapshot
/// of somebody else's state. Same <c>Get</c>/<c>Set</c> as <see cref="IPluginStorage"/>, but its own file next
/// to <c>cockpit.json</c> — so a settings restore no longer writes the source machine's cache over this one's,
/// and a backup carries none of it (AC-1115).
/// </summary>
/// <remarks>
/// There is deliberately no <c>SetSecret</c>/<c>GetSecret</c> here, and that absence is the rule: a cache is not
/// a place for credentials. Its file has neither the encryption at rest nor the backup scrubbing that
/// <see cref="IPluginStorage"/> gives a declared secret. A token belongs in <see cref="IPluginStorage.SetSecret"/>.
/// </remarks>
public interface IPluginCache
{
    /// <summary>Reads back what <see cref="Set{T}"/> stored, or <c>default</c> when nothing is cached under this key.</summary>
    T? Get<T>(string key);

    /// <summary>Caches <paramref name="value"/> as JSON. Never a credential — see the remark on <see cref="IPluginCache"/>.</summary>
    void Set<T>(string key, T value);
}

// What `ICockpitHost.Cache` falls back to on a host that has no cache file behind it — a test double, mostly.
// A cache that only lives as long as the process does is still a working cache. `Shared` is the interface
// default's one instance, so two plugins on such a host share a key space they would not share on the real one.
public sealed class InMemoryPluginCache : IPluginCache
{
    internal static readonly IPluginCache Shared = new InMemoryPluginCache();

    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public T? Get<T>(string key) =>
        _values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

    public void Set<T>(string key, T value) => _values[key] = JsonSerializer.Serialize(value);
}
