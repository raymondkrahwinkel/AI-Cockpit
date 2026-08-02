using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the shared-project sources plugins register (<c>ICockpitHost.AddSharedProjectSource</c>, AC-245), so the
/// Projects workspace can list what they offer without depending on the plugins that contribute them. A registry of
/// its own, the same shape as <see cref="IProjectMemorySourceRegistry"/>. Empty until a plugin that shares project
/// definitions elsewhere is installed.
/// </summary>
public interface ISharedProjectSourceRegistry
{
    /// <summary>Records a source. A key that is already registered is refused, first one wins.</summary>
    /// <returns>False when another plugin already contributes this key — the caller says so; nothing throws.</returns>
    bool Register(ISharedProjectSource source);

    /// <summary>Withdraws the source registered under <paramref name="key"/>. A no-op when nothing is registered under it.</summary>
    void Remove(string key);

    /// <summary>Every source registered so far, in registration order.</summary>
    IReadOnlyList<ISharedProjectSource> Sources { get; }
}

internal sealed class SharedProjectSourceRegistry : ISharedProjectSourceRegistry, ISingletonService
{
    private readonly Dictionary<string, ISharedProjectSource> _sources = new(StringComparer.Ordinal);

    public IReadOnlyList<ISharedProjectSource> Sources => [.. _sources.Values];

    public bool Register(ISharedProjectSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Key) || _sources.ContainsKey(source.Key))
        {
            return false;
        }

        _sources.Add(source.Key, source);
        return true;
    }

    public void Remove(string key) => _sources.Remove(key);
}
