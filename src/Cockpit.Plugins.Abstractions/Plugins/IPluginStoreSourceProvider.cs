namespace Cockpit.Plugins.Abstractions.Plugins;

/// <summary>Supplies store sources while a plugin is loaded; requests are signed in by the handler of each source.</summary>
public interface IPluginStoreSourceProvider
{
    IReadOnlyList<PluginStoreSourceDescriptor> Sources { get; }

    event EventHandler? SourcesChanged;
}
