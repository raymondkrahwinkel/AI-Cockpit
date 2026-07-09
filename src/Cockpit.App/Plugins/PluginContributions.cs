using Avalonia.Controls;

namespace Cockpit.App.Plugins;

/// <summary>An Options-dialog tab a plugin contributes: its title and a factory that builds the tab's content control.</summary>
public sealed record PluginOptionsTab(string Title, Func<Control> CreateView);

/// <summary>A left-menu accordion section a plugin contributes, shown under the session list: its title and a factory that builds the section content.</summary>
public sealed record PluginSideSection(string Title, Func<Control> CreateView);

/// <summary>
/// Where a plugin's contribution points land in the running UI. Implemented by <c>CockpitViewModel</c>
/// (the collections the Options dialog and side menu bind to); an interface so <see cref="CockpitHost"/>
/// and its tests depend on the sink, not the whole cockpit view model.
/// </summary>
public interface IPluginContributionSink
{
    void AddPluginOptionsTab(string title, Func<Control> createView);

    void AddPluginSideSection(string title, Func<Control> createView);
}
