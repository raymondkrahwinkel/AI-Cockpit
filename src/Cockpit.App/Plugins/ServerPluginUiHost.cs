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

// AC-1571: the host a second copy of a plugin's UI part gets, so its own settings view reads and writes the plugin's
// storage on a server and reaches its backend part there. It keeps the settings view and drops every other
// registration (the local copy already made them); window helpers go to the local host, and what acts on this machine does nothing.
internal sealed class ServerPluginUiHost(
    ICockpitUiHost local,
    IPluginStorage storage,
    IPluginUiChannel channel,
    Func<string, CancellationToken, Task<PluginMcpAuthState>> mcpAuthState,
    Func<Task<IReadOnlyList<PluginProfileInfo>>> profiles) : ICockpitUiHost
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

    public Control? CreateEmbeddedSessionView(string paneId) => null;

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        local.ShowDialogAsync(title, createContent, width, height);

    public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
        local.ShowDialogAsync(title, createContent, singleInstanceKey, width, height);

    public Task ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, Action<string>? onStarted = null, Action? onCancelled = null) => Task.CompletedTask;

    public Control CreateMarkdownView(string markdown) => local.CreateMarkdownView(markdown);

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

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") => local.ConfirmAsync(title, message, confirmLabel);

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        local.ShowToast(message, severity, actionLabel, onAction);

    // A grant made here would be remembered on this machine for something that runs on the server.
    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) => Task.FromResult(ConsentDecision.Denied);

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) => Task.FromResult(ConsentDecision.Denied);

    // The server's profiles, which a session the plugin starts there runs on.
    public Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => profiles();

    public Task SendToSessionAsync(string paneId, string text) => Task.CompletedTask;

    public Task InsertIntoSessionAsync(string paneId, string text) => Task.CompletedTask;

    public Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProjectMemoryRow>>([]);

    public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public bool CanSendIntent(string targetPluginId, string action) => false;

    public string? ResolveManagedCliPath(string cliName) => null;

    public Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManagedCliStatus(null, null));

    public Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) =>
        Task.FromResult(ManagedCliInstallResult.Fail("A server's command-line tools are installed on the server."));

    public bool RemoveManagedCli(string cliName) => false;

    public Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) =>
        mcpAuthState(name, cancellationToken);

    // A browser sign-in cannot finish on a server without a screen; an installation token over the plugin's channel is
    // that route (AC-1511), so the view's own fallback answer stands.
    public Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(PluginMcpSignInOutcome.Unavailable);
}
