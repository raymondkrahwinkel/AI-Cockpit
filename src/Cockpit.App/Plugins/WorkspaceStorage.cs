using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// One plugin workspace's slice of its plugin's storage: every key is prefixed with the workspace id, so two
// workspaces of the same type keep separate state while the plugin still owns a single storage section. A thin
// scoping layer over `IPluginStorage` rather than a second persistence mechanism — the plugin's
// own section already round-trips through `cockpit.json`, the same way `WidgetInstanceStorage`
// scopes a placed widget. The prefix is namespaced (`workspace:`) so a plugin that contributes both a
// workspace type and a widget cannot collide their keys.
public sealed class WorkspaceStorage(IPluginStorage inner, string workspaceId) : IPluginStorage
{
    public T? Get<T>(string key) => inner.Get<T>(_Scope(key));

    public void Set<T>(string key, T value) => inner.Set(_Scope(key), value);

    public void SetSecret(string key, string value) => inner.SetSecret(_Scope(key), value);

    public string? GetSecret(string key) => inner.GetSecret(_Scope(key));

    private string _Scope(string key) => $"workspace:{workspaceId}:{key}";
}
