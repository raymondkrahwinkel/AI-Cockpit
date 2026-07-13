using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>A left-menu accordion section a plugin contributes, shown under the session list: its title and a factory that builds the section content.</summary>
public sealed record PluginSideSection(string Title, Func<Control> CreateView);

/// <summary>A left-menu launcher button a plugin contributes: its title and the action run on click (typically opening a dialog).</summary>
public sealed record PluginSideButton(string Title, Action OnInvoke);

/// <summary>A control a plugin contributes to every session's header bar, built once per session from that session's own context (#: session header items).</summary>
public sealed record PluginSessionHeaderItem(Func<IPluginSessionContext, Control> CreateView);

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

    /// <summary>Registers a control shown in every session's header, built per session from that session's own context.</summary>
    void AddPluginSessionHeaderItem(Func<IPluginSessionContext, Control> createView);

    /// <summary>Registers a plugin-contributed keyboard shortcut (#: shortcuts), dispatched alongside the app-action shortcuts.</summary>
    void AddPluginShortcut(PluginShortcut shortcut);

    void AddPluginSettings(string pluginId, Func<Control> createView);

    /// <summary>Registers <paramref name="callback"/> to run when <paramref name="pluginId"/>'s settings are next saved (#52) — see <see cref="ICockpitHost.OnSettingsSaved"/>.</summary>
    void AddSettingsSavedHandler(string pluginId, Action callback);

    /// <summary>Runs every callback registered via <see cref="AddSettingsSavedHandler"/> for <paramref name="pluginId"/> (#52) — called once that plugin's settings dialog Save() has returned true.</summary>
    void NotifySettingsSaved(string pluginId);
}
