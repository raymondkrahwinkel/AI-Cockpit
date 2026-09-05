using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// One companion tool's slice of its plugin's storage: every key is prefixed with the tool id, so a tool's own
// keys never collide with the plugin's top-level keys or another tool's. Mirrors WidgetInstanceStorage.
internal sealed class CompanionToolInstanceStorage(IPluginStorage inner, string toolId) : IPluginStorage
{
    public T? Get<T>(string key) => inner.Get<T>(_Scope(key));

    public void Set<T>(string key, T value) => inner.Set(_Scope(key), value);

    public void SetSecret(string key, string value) => inner.SetSecret(_Scope(key), value);

    public string? GetSecret(string key) => inner.GetSecret(_Scope(key));

    private string _Scope(string key) => $"companion:{toolId}:{key}";
}
