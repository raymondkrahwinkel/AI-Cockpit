using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.App.Plugins;

/// <summary>A left-menu accordion section a plugin contributes, shown under the session list: which plugin it came from (#72 — the operator orders and hides the menu per plugin), its title, and a factory that builds the section content.</summary>
public sealed record PluginSideSection(string PluginId, string Title, Func<Control> CreateView);

/// <summary>A left-menu launcher button a plugin contributes: which plugin it came from (#72), its title, and the action run on click (typically opening a dialog).</summary>
public sealed record PluginSideButton(string PluginId, string Title, Action OnInvoke);

/// <summary>A Sessions-toolbar button a plugin contributes (AC-91): which plugin it came from (#72 — the operator's menu order/hide applies here too), and the action itself (icon, tooltip, on-click).</summary>
public sealed record PluginToolbarAction(string PluginId, ToolbarAction Action);

/// <summary>A control a plugin contributes to every session's header bar, built once per session from that session's own context (#: session header items).</summary>
public sealed record PluginSessionHeaderItem(Func<IPluginSessionContext, Control> CreateView);

/// <summary>
/// One thing a plugin put in the left menu: either a launcher <see cref="Button"/> or an inline <see cref="Section"/>,
/// never both. They share a list so the operator's order (#72) applies across them — a section moved to the top belongs
/// at the top, not below every plugin that happens to contribute a button instead.
/// </summary>
public sealed record PluginMenuEntry(string PluginId, PluginSideButton? Button, PluginSideSection? Section);

/// <summary>
/// A plugin's settings view: which plugin it belongs to, the plugin's own name (what the dialog is titled,
/// wherever it is opened from — the manager's gear, a left-menu gear, or the plugin itself), and the factory
/// that builds it.
/// </summary>
public sealed record PluginSettingsRegistration(string PluginId, string PluginName, Func<Control> CreateView);

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

    /// <summary>Registers a plugin's source of supervised background activities shown in the status bar (AC-82), with an operator-only Kill per item.</summary>
    void AddSupervisedActivityProvider(ISupervisedActivitySource source);

    /// <summary>Registers a Sessions-toolbar button (AC-91) — a global quick action shown next to the workspace gear.</summary>
    void AddToolbarAction(string pluginId, ToolbarAction action);

    /// <summary>Registers a plugin-contributed keyboard shortcut (#: shortcuts), dispatched alongside the app-action shortcuts.</summary>
    void AddPluginShortcut(PluginShortcut shortcut);

    /// <summary>Registers <paramref name="pluginId"/>'s settings view, titled after <paramref name="pluginName"/> wherever it is opened from.</summary>
    void AddPluginSettings(string pluginId, string pluginName, Func<Control> createView);

    /// <summary>Whether <paramref name="pluginId"/> registered a settings view — what the gears (left menu, dialog chrome) ask before offering to open one.</summary>
    bool HasPluginSettings(string pluginId);

    /// <summary>
    /// Opens <paramref name="pluginId"/>'s settings dialog: the one way in, whether the operator came from the
    /// plugin manager's gear, the gear on its left-menu button, the gear on one of its dialogs, or the plugin
    /// asked for it itself (<see cref="ICockpitHost.ShowSettingsAsync"/>). Saving runs the plugin's
    /// settings-saved handlers, so a settings change lands the same way from all of them. Does nothing when the
    /// plugin registered no settings view.
    /// </summary>
    Task OpenPluginSettingsAsync(string pluginId);

    /// <summary>Registers <paramref name="callback"/> to run when <paramref name="pluginId"/>'s settings are next saved (#52) — see <see cref="ICockpitHost.OnSettingsSaved"/>.</summary>
    void AddSettingsSavedHandler(string pluginId, Action callback);

    /// <summary>Runs every callback registered via <see cref="AddSettingsSavedHandler"/> for <paramref name="pluginId"/> (#52) — called once that plugin's settings dialog Save() has returned true.</summary>
    void NotifySettingsSaved(string pluginId);

    /// <summary>Applies the left-menu order/visibility the plugin manager just persisted for <paramref name="pluginId"/> (#72), so the sidebar re-renders without a restart.</summary>
    void ApplyPluginMenuPreference(string pluginId, int menuOrder, bool hiddenInMenu);
}
