using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// One workspace's slice of its plugin's storage, keys prefixed with the workspace id so two
// workspaces of the same type keep separate state (mirrors `WidgetInstanceStorage`). Prefix is
// namespaced (`workspace:`) so a plugin contributing both a workspace type and a widget can't collide keys.
public sealed class WorkspaceStorage(IPluginStorage inner, string workspaceId) : IPluginStorage
{
    public T? Get<T>(string key) => inner.Get<T>(_Scope(key));

    public void Set<T>(string key, T value) => inner.Set(_Scope(key), value);

    public void SetSecret(string key, string value) => inner.SetSecret(_Scope(key), value);

    public string? GetSecret(string key) => inner.GetSecret(_Scope(key));

    private string _Scope(string key) => $"workspace:{workspaceId}:{key}";
}
