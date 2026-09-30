using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the "add/edit profile" config view each session provider's UI part registered, keyed by provider id.
/// </summary>
public interface IPluginProviderConfigViews
{
    /// <summary>
    /// Registers the config view factory for <paramref name="providerId"/>; a later call for the same id replaces it.
    /// </summary>
    void Register(string providerId, Func<string?, IPluginProviderConfigView> createView);

    /// <summary>
    /// The config view factory of <paramref name="providerId"/>, or null when its UI part registered none.
    /// </summary>
    Func<string?, IPluginProviderConfigView>? Find(string providerId);
}

internal sealed class PluginProviderConfigViews : IPluginProviderConfigViews, ISingletonService
{
    private readonly ConcurrentDictionary<string, Func<string?, IPluginProviderConfigView>> _views = new(StringComparer.Ordinal);

    public void Register(string providerId, Func<string?, IPluginProviderConfigView> createView) =>
        _views[providerId] = createView;

    public Func<string?, IPluginProviderConfigView>? Find(string providerId) =>
        _views.GetValueOrDefault(providerId);
}
