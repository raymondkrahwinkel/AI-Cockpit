namespace Cockpit.Plugins.Abstractions;

/// <summary>Per-plugin key/value storage, persisted in a plugin-scoped section of the host's <c>cockpit.json</c>. Values are serialized as JSON.</summary>
public interface IPluginStorage
{
    T? Get<T>(string key);

    void Set<T>(string key, T value);
}
