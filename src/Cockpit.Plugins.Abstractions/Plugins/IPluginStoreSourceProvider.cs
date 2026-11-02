namespace Cockpit.Plugins.Abstractions.Plugins;

/// <summary>Supplies store sources while a plugin is loaded; tokens are resolved only when a request is sent.</summary>
public interface IPluginStoreSourceProvider
{
    IReadOnlyList<PluginStoreSourceDescriptor> Sources { get; }

    event EventHandler? SourcesChanged;
}
