using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// One placed widget's slice of its plugin's storage: every key is prefixed with the instance id, so two
/// System Monitors on the same dashboard keep separate config while the plugin still owns a single storage
/// section. A thin scoping layer over <see cref="IPluginStorage"/> rather than a second persistence
/// mechanism — the plugin's own section already round-trips through <c>cockpit.json</c>, and widget config
/// has no reason to live anywhere else.
/// </summary>
/// <remarks>
/// The prefix is deliberately namespaced (<c>widget:</c>): a plugin that contributes both a widget and a
/// side-menu button shares one storage, and its own top-level keys must not be able to collide with an
/// instance's.
/// </remarks>
public sealed class WidgetInstanceStorage(IPluginStorage inner, string instanceId) : IPluginStorage
{
    public T? Get<T>(string key) => inner.Get<T>(_Scope(key));

    public void Set<T>(string key, T value) => inner.Set(_Scope(key), value);

    public void SetSecret(string key, string value) => inner.SetSecret(_Scope(key), value);

    public string? GetSecret(string key) => inner.GetSecret(_Scope(key));

    /// <summary>
    /// This instance's own keys, unprefixed — what an export carries. Returns nothing unless the plugin's
    /// storage can be snapshotted (a test double, for instance), since there is no way to enumerate through the
    /// plugin contract and no reason to add one.
    /// </summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        var prefix = _Scope(string.Empty);
        return inner is not PluginStorage storage
            ? new Dictionary<string, string>()
            : storage.Snapshot()
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key[prefix.Length..], entry => entry.Value);
    }

    private string _Scope(string key) => $"widget:{instanceId}:{key}";
}
