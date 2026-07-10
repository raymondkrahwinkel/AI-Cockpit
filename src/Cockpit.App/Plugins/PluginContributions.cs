using Avalonia.Controls;

namespace Cockpit.App.Plugins;

/// <summary>A left-menu accordion section a plugin contributes, shown under the session list: its title and a factory that builds the section content.</summary>
public sealed record PluginSideSection(string Title, Func<Control> CreateView);

/// <summary>A left-menu launcher button a plugin contributes: its title and the action run on click (typically opening a dialog).</summary>
public sealed record PluginSideButton(string Title, Action OnInvoke);

/// <summary>
/// Where a plugin's contribution points land in the running UI. Implemented by <c>CockpitViewModel</c>
/// (the collections/registry the side menu and plugin manager bind to); an interface so <c>CockpitHost</c>
/// and its tests depend on the sink, not the whole cockpit view model. Settings are keyed by plugin id so
/// the manager can show a gear for the plugin they belong to.
/// </summary>
public interface IPluginContributionSink
{
    void AddPluginSideSection(string title, Func<Control> createView);

    void AddPluginSideButton(string title, Action onInvoke);

    void AddPluginSettings(string pluginId, Func<Control> createView);
}
