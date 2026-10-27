using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

// AC-1571: the host a second copy of a plugin's UI part gets, so its own views read and write the plugin's storage on a
// server and reach its backend part and managed tools there (AC-1585: a provider's profile view too). Every other
// registration is dropped (the local copy made them); window helpers go to the local host.
internal sealed class ServerPluginUiHost(
    ICockpitUiHost local,
    IPluginStorage storage,
    IPluginUiChannel channel,
    Func<string, CancellationToken, Task<PluginMcpAuthState>> mcpAuthState,
    Func<string, CancellationToken, Task<PluginMcpSignInOutcome>> mcpSignIn,
    Func<string, CancellationToken, Task> mcpSignOut,
    Func<Task<IReadOnlyList<PluginProfileInfo>>> profiles,
    Func<Task<IReadOnlyList<PluginProjectInfo>>> projects,
    ServerManagedClis managedClis) : ICockpitUiHost
{
    private readonly List<Action> _saved = [];
    private readonly Dictionary<string, Func<string?, IPluginProviderConfigView>> _providerViews = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Func<string?, IPluginProviderConfigView>> ProviderConfigViews => _providerViews;

    public Func<Control>? CreateSettingsView { get; private set; }

    public IReadOnlyList<Action> SettingsSaved => _saved;

    public IPluginUiChannel Channel => channel;

    public IPluginStorage Storage => storage;

    public void AddSettings(Func<Control> createView) => CreateSettingsView = createView;

    public void AddSettings(Func<Control> createView, string category) => CreateSettingsView = createView;

    public bool HasSettings => CreateSettingsView is not null;

    public Task ShowSettingsAsync() => Task.CompletedTask;

    public void OnSettingsSaved(Action callback) => _saved.Add(callback);

    public void AddServerSettings(Func<IPluginUiChannel, Control> createView)
    {
    }

    public void AddSideMenuButton(string title, Action onInvoke)
    {
    }

    public SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke) => new();

    public void AddSideMenuSection(string title, Func<Control> createView)
    {
    }

    public void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView)
    {
    }

    public void AddSessionBanner(Func<IPluginSessionContext, Control> createView)
    {
    }

    public void AddSessionHeaderAction(PluginSessionAction action)
    {
    }

    public void AddToolbarAction(ToolbarAction action)
    {
    }

    public void AddShortcut(PluginShortcut shortcut)
    {
    }

    public void AddConversationPicker(ConversationPickerRegistration picker)
    {
    }

    // AC-1585: kept, so the profile editor opened on the server gets the provider's own view against the server.
    public void AddProviderConfigView(string providerId, Func<string?, IPluginProviderConfigView> createView) =>
        _providerViews[providerId] = createView;

    public void AddWidget(WidgetRegistration registration)
    {
    }

    public void AddDockPanel(DockPanelRegistration registration)
    {
    }

    public void AddCompanionTool(CompanionToolRegistration registration)
    {
    }

    public void AddWorkspaceType(WorkspaceTypeRegistration registration)
    {
    }

    public Task OpenWorkspaceAsync(string workspaceTypeId) => Task.CompletedTask;

    public Task OpenWorkspaceAsync(string workspaceTypeId, WorkspaceOpenOptions options) => Task.CompletedTask;

    public Task OpenDockPanelAsync(string panelId) => Task.CompletedTask;

    public void SetStatusText(string? text)
    {
    }

    public Control? CreateEmbeddedSessionView(string paneId) => null;

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        local.ShowDialogAsync(title, createContent, width, height);

    public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
        local.ShowDialogAsync(title, createContent, singleInstanceKey, width, height);

    public Task ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, Action<string>? onStarted = null, Action? onCancelled = null) => Task.CompletedTask;

    public Control CreateMarkdownView(string markdown) => local.CreateMarkdownView(markdown);

    public IMarkdownEditor? CreateMarkdownEditor(string markdown) => local.CreateMarkdownEditor(markdown);

    public Control CreateHelpHint(string article, string? section = null, string? label = null) => local.CreateHelpHint(article, section, label);

    public void OpenHelp(string article, string? section = null) => local.OpenHelp(article, section);

    public bool HasHelp(string article, string? section = null) => local.HasHelp(article, section);

    public string? ActivePaneId => null;

    public string? ActiveSessionWorkingDirectory => null;

    public SessionUsageSnapshot? ActiveSessionUsage => null;

    public event EventHandler? ActiveSessionChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public event EventHandler? ActiveSessionUsageChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public Task SetClipboardTextAsync(string text) => local.SetClipboardTextAsync(text);

    public void AddLinkHandler(LinkHandlerRegistration registration)
    {
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") => local.ConfirmAsync(title, message, confirmLabel);

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        local.ShowToast(message, severity, actionLabel, onAction);

    // A grant made here would be remembered on this machine for something that runs on the server.
    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) => Task.FromResult(ConsentDecision.Denied);

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) => Task.FromResult(ConsentDecision.Denied);

    // The server's profiles, which a session the plugin starts there runs on.
    public Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => profiles();

    public Task<IReadOnlyList<PluginProjectInfo>> GetProjectsAsync() => projects();

    public Task SendToSessionAsync(string paneId, string text) => Task.CompletedTask;

    public Task InsertIntoSessionAsync(string paneId, string text) => Task.CompletedTask;

    public Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProjectMemoryRow>>([]);

    public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public bool CanSendIntent(string targetPluginId, string action) => false;

    // AC-1585: the server's managed copies, as the plugin's host there has them.
    public string? ResolveManagedCliPath(string cliName) => managedClis.Resolve(cliName);

    public Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) =>
        managedClis.StatusAsync(cliName, cancellationToken);

    public Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) =>
        managedClis.InstallAsync(cliName, cancellationToken);

    public bool RemoveManagedCli(string cliName) => managedClis.Remove(cliName);

    public Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) =>
        managedClis.AutoUpdateAsync(cliName, cancellationToken);

    public Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) =>
        managedClis.SetAutoUpdateAsync(cliName, enabled, cancellationToken);

    public Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) =>
        mcpAuthState(name, cancellationToken);

    // AC-1571 B2: the server runs the sign-in and keeps the token; this desktop only does the browser step.
    public Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        mcpSignIn(name, cancellationToken);

    // AC-1660: the server keeps the token, so the server forgets it.
    public Task SignOutMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        mcpSignOut(name, cancellationToken);
}
