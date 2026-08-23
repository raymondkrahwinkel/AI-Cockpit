using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// One placed widget's slice of its plugin's storage: every key gets the instance id prefixed, so two
// instances keep separate config within one shared `IPluginStorage` section. Namespaced (`widget:`)
// so a plugin's own top-level keys can't collide with an instance's.
public sealed class WidgetInstanceStorage(IPluginStorage inner, string instanceId) : IPluginStorage
{
    public T? Get<T>(string key) => inner.Get<T>(_Scope(key));

    public void Set<T>(string key, T value) => inner.Set(_Scope(key), value);

    public void SetSecret(string key, string value) => inner.SetSecret(_Scope(key), value);

    public string? GetSecret(string key) => inner.GetSecret(_Scope(key));

    // This instance's own keys, unprefixed — what an export carries. Returns nothing unless the plugin's
    // storage can be snapshotted (a test double, for instance), since there is no way to enumerate through the
    // plugin contract and no reason to add one.
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
