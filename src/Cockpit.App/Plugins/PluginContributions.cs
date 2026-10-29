using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.StatusBar;
using Material.Icons;

namespace Cockpit.App.Plugins;

// A left-menu accordion section a plugin contributes, shown under the session list: which plugin it came from (#72 — the operator orders and hides the menu per plugin), its title, and a factory that builds the section content.
public sealed record PluginSideSection(string PluginId, string Title, Func<Control> CreateView);

// A left-menu launcher button a plugin contributes (#72): which plugin, its title, and the click action.
// `Badge` (AC-516) is the counter handle from `AddSideMenuButtonWithBadge`, or null for a plain button.
public sealed record PluginSideButton(
    string PluginId,
    string Title,
    Action OnInvoke,
    SideMenuButtonBadge? Badge = null,
    MaterialIconKind? IconKind = null);

// A Sessions-toolbar button a plugin contributes (AC-91): which plugin it came from (#72 — the operator's menu order/hide applies here too), and the action itself (icon, tooltip, on-click).
public sealed record PluginToolbarAction(string PluginId, ToolbarAction Action);

// AC-1711: the one line of text a plugin shows in the status bar (`ICockpitUiHost.SetStatusText`), keyed by plugin.
public sealed record PluginStatusText(string PluginId, string Text, SideMenuButtonBadgeTone? Tone = null);

// A control a plugin contributes to every session's header bar, built once per session from that session's own context (#: session header items).
public sealed record PluginSessionHeaderItem(Func<IPluginSessionContext, Control> CreateView);

// A control a plugin contributes to every session's banner strip under the transcript, built once per session from
// that session's own context (AC-802) — the wider counterpart to PluginSessionHeaderItem, for PR/CI status and the
// like.
public sealed record PluginSessionBannerItem(Func<IPluginSessionContext, Control> CreateView);

// One thing a plugin put in the left menu: either a launcher `Button` or an inline `Section`,
// never both. They share a list so the operator's order (#72) applies across them — a section moved to the top belongs
// at the top, not below every plugin that happens to contribute a button instead.
public sealed record PluginMenuEntry(string PluginId, PluginSideButton? Button, PluginSideSection? Section);

// AC-1763: one sidebar card. A plugin's own buttons and sections share the card named after it (`plugin:{id}`);
// plugins that contributed a single button and nothing else share the one "Plugins" card (`plugins`).
public sealed record PluginMenuGroup(
    string Key,
    string Title,
    string? PluginId,
    IReadOnlyList<PluginSideButton> Buttons,
    IReadOnlyList<PluginSideSection> Sections)
{
    public const string CollectedKey = "plugins";

    // What the header shows while the card is folded: the numeric badges added up. A text badge ("0u25 vandaag") has
    // no number to add, so it only shows on its own row.
    public int BadgeTotal => Buttons.Sum(button => button.Badge?.Primary ?? 0);

    // `ordered` is the operator's order (AC-937's list); cards keep the position of their plugin's first entry, and
    // the collected card comes last.
    public static IReadOnlyList<PluginMenuGroup> Group(IEnumerable<PluginMenuEntry> ordered, Func<string, string> displayName)
    {
        var groups = new List<PluginMenuGroup>();
        var collected = new List<PluginSideButton>();
        foreach (var plugin in ordered.GroupBy(entry => entry.PluginId))
        {
            var buttons = plugin.Select(entry => entry.Button).OfType<PluginSideButton>().ToList();
            var sections = plugin.Select(entry => entry.Section).OfType<PluginSideSection>().ToList();
            if (sections.Count == 0 && buttons.Count == 1)
            {
                collected.AddRange(buttons);
            }
            else
            {
                groups.Add(new PluginMenuGroup($"plugin:{plugin.Key}", displayName(plugin.Key), plugin.Key, buttons, sections));
            }
        }

        if (collected.Count > 0)
        {
            groups.Add(new PluginMenuGroup(CollectedKey, "Plugins", null, collected, []));
        }

        return groups;
    }
}

// A plugin's settings view: which plugin it belongs to, the plugin's own name (what the dialog is titled,
// wherever it is opened from — the manager's gear, a left-menu gear, or the plugin itself), and the factory
// that builds it. No factory (AC-289) is a plugin whose page holds only what the host draws from its declarations.
public sealed record PluginSettingsRegistration(string PluginId, string PluginName, Func<Control>? CreateView, string? Category = null);

/// <summary>
/// Where a plugin's contribution points land in the running UI. Implemented by <c>CockpitViewModel</c>; an
/// interface so <c>CockpitHost</c> and its tests depend on the sink, not the whole view model. Settings are
/// keyed by plugin id so the manager can show a gear for the plugin they belong to.
/// </summary>
public interface IPluginContributionSink
{
    void AddPluginSideSection(string pluginId, string title, Func<Control> createView);

    void AddPluginSideButton(string pluginId, string title, Action onInvoke);

    /// <summary>
    /// Same as <see cref="AddPluginSideButton(string, string, Action)"/>, carrying the badge handle (AC-516) from
    /// <see cref="ICockpitHost.AddSideMenuButtonWithBadge"/>. A default overload, forwarding to the badge-less
    /// member, so existing <see cref="IPluginContributionSink"/> test fakes keep compiling without wiring the badge.
    /// </summary>
    void AddPluginSideButton(string pluginId, string title, Action onInvoke, SideMenuButtonBadge? badge) =>
        AddPluginSideButton(pluginId, title, onInvoke);

    void AddPluginSideButton(
        string pluginId,
        string title,
        Action onInvoke,
        SideMenuButtonBadge? badge,
        MaterialIconKind iconKind) =>
        AddPluginSideButton(pluginId, title, onInvoke, badge);

    /// <summary>
    /// Registers a control shown in every session's header, built per session from that session's own context.
    /// </summary>
    void AddPluginSessionHeaderItem(Func<IPluginSessionContext, Control> createView);

    /// <summary>
    /// Registers a control shown in every session's banner strip under the transcript (AC-802), built per session
    /// from that session's own context.
    /// </summary>
    void AddPluginSessionBannerItem(Func<IPluginSessionContext, Control> createView);

    /// <summary>
    /// Registers an action in every session header's menu — one menu for all plugins, rather than a button each.
    /// </summary>
    void AddPluginSessionHeaderAction(PluginSessionAction action);

    /// <summary>
    /// Registers a plugin's source of supervised background activities shown in the status bar (AC-82), with an operator-only Kill per item.
    /// </summary>
    void AddSupervisedActivityProvider(ISupervisedActivitySource source);

    /// <summary>
    /// Registers a Sessions-toolbar button (AC-91) — a global quick action shown next to the workspace gear.
    /// </summary>
    void AddToolbarAction(string pluginId, ToolbarAction action);

    /// <summary>
    /// Registers a plugin-contributed keyboard shortcut (#: shortcuts), dispatched alongside the app-action shortcuts.
    /// </summary>
    void AddPluginShortcut(PluginShortcut shortcut);

    /// <summary>
    /// Registers <paramref name="pluginId"/>'s settings view, titled after <paramref name="pluginName"/> wherever it is opened from.
    /// </summary>
    void AddPluginSettings(string pluginId, string pluginName, Func<Control> createView);

    /// <summary>
    /// Same as <see cref="AddPluginSettings(string, string, Func{Control})"/>, carrying the sidebar category
    /// (AC-1030). A default overload, forwarding to the category-less member, so existing
    /// <see cref="IPluginContributionSink"/> test fakes keep compiling without wiring a category.
    /// </summary>
    void AddPluginSettings(string pluginId, string pluginName, Func<Control> createView, string? category) =>
        AddPluginSettings(pluginId, pluginName, createView);

    /// <summary>
    /// Registers <paramref name="pluginId"/>'s view for a server it is installed on (AC-1511), opened from Admin →
    /// Plugins with a channel to the plugin's backend part on that server. Default no-op for test fakes.
    /// </summary>
    void AddPluginServerSettings(string pluginId, string pluginName, Func<IPluginUiChannel, Control> createView)
    {
    }

    /// <summary>
    /// Whether <paramref name="pluginId"/> registered a settings view — what the gears (left menu, dialog chrome) ask before offering to open one.
    /// </summary>
    bool HasPluginSettings(string pluginId);

    /// <summary>
    /// Opens <paramref name="pluginId"/>'s settings dialog: the one way in, whether from the plugin manager's gear,
    /// a left-menu or dialog gear, or the plugin itself (<see cref="ICockpitHost.ShowSettingsAsync"/>). Saving runs
    /// the plugin's settings-saved handlers the same way from all of them; does nothing with no settings view.
    /// </summary>
    Task OpenPluginSettingsAsync(string pluginId);

    /// <summary>
    /// Registers <paramref name="callback"/> to run when <paramref name="pluginId"/>'s settings are next saved (#52) — see <see cref="ICockpitHost.OnSettingsSaved"/>.
    /// </summary>
    void AddSettingsSavedHandler(string pluginId, Action callback);

    /// <summary>
    /// Runs every callback registered via <see cref="AddSettingsSavedHandler"/> for <paramref name="pluginId"/> (#52) — called once the host has run the write <see cref="IPluginSettingsView.TryStage"/> handed it, never at staging time (AC-1004).
    /// </summary>
    void NotifySettingsSaved(string pluginId);

    /// <summary>
    /// Applies the left-menu order/visibility the plugin manager just persisted for <paramref name="pluginId"/> (#72), so the sidebar re-renders without a restart.
    /// </summary>
    void ApplyPluginMenuPreference(string pluginId, int menuOrder, bool hiddenInMenu);

    /// <summary>
    /// Same as <see cref="ApplyPluginMenuPreference(string, int, bool)"/>, also carrying whether the plugin is
    /// pinned top-level rather than collapsed under "Plugins ›" (AC-937). Forwards to the three-argument member,
    /// dropping the pin, so existing test fakes keep compiling; only the app's own sink acts on it.
    /// </summary>
    void ApplyPluginMenuPreference(string pluginId, int menuOrder, bool hiddenInMenu, bool pinnedToSidebar) =>
        ApplyPluginMenuPreference(pluginId, menuOrder, hiddenInMenu);
}
