using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>A left-menu accordion section a plugin contributes, shown under the session list: which plugin it came from (#72 — the operator orders and hides the menu per plugin), its title, and a factory that builds the section content.</summary>
public sealed record PluginSideSection(string PluginId, string Title, Func<Control> CreateView);

/// <summary>A left-menu launcher button a plugin contributes: which plugin it came from (#72), its title, and the action run on click (typically opening a dialog).</summary>
public sealed record PluginSideButton(string PluginId, string Title, Action OnInvoke);

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
    void AddPluginSideSection(string pluginId, string title, Func<Control> createView);

    void AddPluginSideButton(string pluginId, string title, Action onInvoke);

    /// <summary>Registers a control shown in every session's header, built per session from that session's own context.</summary>
    void AddPluginSessionHeaderItem(Func<IPluginSessionContext, Control> createView);

    /// <summary>Registers an action in every session header's menu — one menu for all plugins, rather than a button each.</summary>
    void AddPluginSessionHeaderAction(PluginSessionAction action);

    /// <summary>Registers a plugin-contributed keyboard shortcut (#: shortcuts), dispatched alongside the app-action shortcuts.</summary>
    void AddPluginShortcut(PluginShortcut shortcut);

    void AddPluginSettings(string pluginId, Func<Control> createView);

    /// <summary>Registers <paramref name="callback"/> to run when <paramref name="pluginId"/>'s settings are next saved (#52) — see <see cref="ICockpitHost.OnSettingsSaved"/>.</summary>
    void AddSettingsSavedHandler(string pluginId, Action callback);

    /// <summary>Runs every callback registered via <see cref="AddSettingsSavedHandler"/> for <paramref name="pluginId"/> (#52) — called once that plugin's settings dialog Save() has returned true.</summary>
    void NotifySettingsSaved(string pluginId);

    /// <summary>Applies the left-menu order/visibility the plugin manager just persisted for <paramref name="pluginId"/> (#72), so the sidebar re-renders without a restart.</summary>
    void ApplyPluginMenuPreference(string pluginId, int menuOrder, bool hiddenInMenu);
}
