using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugins.Abstractions.UI;

// AC-1389: what the host offers a plugin's UI part in ICockpitPluginUi.InitializeUi. A member is here when it
// means nothing without a window; the rest is ICockpitHost's. No Services on purpose: the UI part reaches its
// backend part through Channel only, so what works in-process keeps working when the backend is remote.
public interface ICockpitUiHost
{
    /// <summary>
    /// This plugin's own channel to its backend part: invoke the actions it handles, subscribe to what it publishes.
    /// </summary>
    IPluginUiChannel Channel { get; }

    /// <summary>
    /// The plugin's storage — the same slice its backend part reads through <see cref="ICockpitHost.Storage"/>.
    /// </summary>
    IPluginStorage Storage { get; }

    /// <summary>
    /// Registers the plugin's settings view, opened from the gear next to the plugin in the plugin manager. Call at most once.
    /// </summary>
    void AddSettings(Func<Control> createView);

    /// <summary>
    /// Registers the settings view under <paramref name="category"/> in the Options window's navigation.
    /// </summary>
    void AddSettings(Func<Control> createView, string category);

    /// <summary>
    /// Whether this plugin registered a settings view.
    /// </summary>
    bool HasSettings { get; }

    /// <summary>
    /// Opens the Options window on this plugin's settings.
    /// </summary>
    Task ShowSettingsAsync();

    /// <summary>
    /// Runs <paramref name="callback"/> after the operator saved this plugin's settings.
    /// </summary>
    void OnSettingsSaved(Action callback);

    /// <summary>
    /// Adds a launcher button to the left menu; clicking runs <paramref name="onInvoke"/>.
    /// </summary>
    void AddSideMenuButton(string title, Action onInvoke);

    /// <summary>
    /// Adds a launcher button to the left menu with a live counter the plugin updates through the returned badge.
    /// </summary>
    SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke);

    /// <summary>
    /// Adds an inline section to the left menu, under the session list.
    /// </summary>
    void AddSideMenuSection(string title, Func<Control> createView);

    /// <summary>
    /// Adds a small control to every session's header bar, built once per session panel on the UI thread.
    /// </summary>
    void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView);

    /// <summary>
    /// Adds a banner under every session's transcript, built once per session panel on the UI thread.
    /// </summary>
    void AddSessionBanner(Func<IPluginSessionContext, Control> createView);

    /// <summary>
    /// Adds an action to the menu in every session's header, handed the session it was invoked from.
    /// </summary>
    void AddSessionHeaderAction(PluginSessionAction action);

    /// <summary>
    /// Adds a button to the Sessions toolbar.
    /// </summary>
    void AddToolbarAction(ToolbarAction action);

    /// <summary>
    /// Registers a keyboard shortcut.
    /// </summary>
    void AddShortcut(PluginShortcut shortcut);

    /// <summary>
    /// Registers a way to pick an earlier conversation to resume from the New-session dialog.
    /// </summary>
    void AddConversationPicker(ConversationPickerRegistration picker);

    /// <summary>
    /// Registers the add/edit-profile view for <paramref name="providerId"/>, one of this plugin's session
    /// providers; the argument is the profile's config JSON when editing, null when adding. Replaces the view
    /// factory the backend part put on its provider registration.
    /// </summary>
    void AddProviderConfigView(string providerId, Func<string?, IPluginProviderConfigView> createView);

    /// <summary>
    /// Contributes a panel type to the dashboard.
    /// </summary>
    void AddWidget(WidgetRegistration registration);

    /// <summary>
    /// Contributes a panel to the dock rail.
    /// </summary>
    void AddDockPanel(DockPanelRegistration registration);

    /// <summary>
    /// Contributes a tool to the companion window.
    /// </summary>
    void AddCompanionTool(CompanionToolRegistration registration);

    /// <summary>
    /// Contributes a workspace type the operator can open as a tab.
    /// </summary>
    void AddWorkspaceType(WorkspaceTypeRegistration registration);

    /// <summary>
    /// Opens (or focuses) a workspace of the type this plugin registered under <paramref name="workspaceTypeId"/>.
    /// </summary>
    Task OpenWorkspaceAsync(string workspaceTypeId);

    /// <summary>
    /// The live view of the embedded session <paramref name="paneId"/> (see <see cref="IEmbeddedSession"/>), the
    /// same control on every call, and a control has one parent, so place it in one spot. Null when no such
    /// embedded session is open; a session in the grid is not embedded.
    /// </summary>
    Control? CreateEmbeddedSessionView(string paneId);

    /// <summary>
    /// Shows <paramref name="createContent"/> in a dialog and completes when it closes.
    /// </summary>
    Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560);

    /// <summary>
    /// Shows a dialog, or focuses the one already open under <paramref name="singleInstanceKey"/>.
    /// </summary>
    Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560);

    /// <summary>
    /// Opens the New-session dialog, optionally prefilled; <paramref name="onStarted"/> gets the new pane id.
    /// </summary>
    Task ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, Action<string>? onStarted = null, Action? onCancelled = null);

    /// <summary>
    /// Renders <paramref name="markdown"/> the way the cockpit renders its own.
    /// </summary>
    Control CreateMarkdownView(string markdown);

    /// <summary>
    /// A small "?" that opens <paramref name="article"/> in the help window; invisible when there is no such article.
    /// </summary>
    Control CreateHelpHint(string article, string? section = null, string? label = null);

    /// <summary>
    /// Opens <paramref name="article"/> in the help window.
    /// </summary>
    void OpenHelp(string article, string? section = null);

    /// <summary>
    /// Whether the help window has <paramref name="article"/> (and <paramref name="section"/>).
    /// </summary>
    bool HasHelp(string article, string? section = null);

    /// <summary>
    /// The pane id of the session this window has selected, or null when none is. A plugin that acts on it passes
    /// it on by id: the backend part has no active session.
    /// </summary>
    string? ActivePaneId { get; }

    /// <summary>
    /// The working directory of the session this window has selected, or null.
    /// </summary>
    string? ActiveSessionWorkingDirectory { get; }

    /// <summary>
    /// The live usage of the session this window has selected, or null.
    /// </summary>
    SessionUsageSnapshot? ActiveSessionUsage { get; }

    /// <summary>
    /// Raised when this window selects another session.
    /// </summary>
    event EventHandler? ActiveSessionChanged;

    /// <summary>
    /// Puts <paramref name="text"/> on this machine's clipboard.
    /// </summary>
    Task SetClipboardTextAsync(string text);

    /// <summary>
    /// Asks the operator to confirm; true when they did.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm");

    /// <summary>
    /// Shows a toast, optionally with one action.
    /// </summary>
    void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null);

    /// <summary>
    /// Asks the host's consent gate — see <see cref="ICockpitHost.RequestConsentAsync(ConsentRequest)"/>.
    /// </summary>
    Task<ConsentDecision> RequestConsentAsync(ConsentRequest request);

    /// <summary>
    /// Asks the host's consent gate, denied when <paramref name="cancellationToken"/> fires first.
    /// </summary>
    Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The session profiles the operator has.
    /// </summary>
    Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync();

    /// <summary>
    /// Sends <paramref name="text"/> to the session <paramref name="paneId"/>, as if typed and submitted.
    /// </summary>
    Task SendToSessionAsync(string paneId, string text);

    /// <summary>
    /// Places <paramref name="text"/> in the input of the session <paramref name="paneId"/> without sending it, so the
    /// operator can still edit it (AC-1397) — the per-pane successor of <c>ICockpitActions.InjectIntoActiveSessionAsync</c>.
    /// Does nothing for an unknown pane.
    /// </summary>
    Task InsertIntoSessionAsync(string paneId, string text);

    /// <summary>
    /// The project-memory rows of the session <paramref name="paneId"/>'s project.
    /// </summary>
    Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an intent to another plugin — see <see cref="ICockpitHost.SendIntent"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data);

    /// <summary>
    /// Whether another plugin handles <paramref name="action"/> — see <see cref="ICockpitHost.CanSendIntent"/>.
    /// </summary>
    bool CanSendIntent(string targetPluginId, string action);

    /// <summary>
    /// The path of a managed CLI this plugin registered, or null while it is not installed.
    /// </summary>
    string? ResolveManagedCliPath(string cliName);

    /// <summary>
    /// The installed and latest version of a managed CLI.
    /// </summary>
    Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs (or updates) a managed CLI.
    /// </summary>
    Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a managed CLI this plugin registered. Returns false when there was nothing installed to remove.
    /// </summary>
    bool RemoveManagedCli(string cliName);

    /// <summary>
    /// Whether a managed CLI updates itself.
    /// </summary>
    Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets whether a managed CLI updates itself.
    /// </summary>
    Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the MCP server <paramref name="name"/> this plugin contributed is signed in.
    /// </summary>
    Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the sign-in of the MCP server <paramref name="name"/> this plugin contributed.
    /// </summary>
    Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default);
}
