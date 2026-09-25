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

namespace Cockpit.Plugin.Discord.Tests;

// AC-1394: replaces the old FakeCockpitHost now that DiscordChannelSettingsControl's constructor takes the UI
// host, not the backend one. Same idea as that fake: only what the control's constructor reaches — its storage,
// and the AC-1033 `?` help hint — gets a real answer; nothing here has a default implementation to fall back on
// (unlike ICockpitHost), so every other member throws, since no test reaches it.
internal sealed class FakeCockpitUiHost : ICockpitUiHost
{
    public IPluginUiChannel Channel => throw new NotSupportedException("No test reaches the channel.");

    public IPluginStorage Storage { get; } = new FakePluginStorage();

    public bool HasSettings => throw new NotSupportedException("No test reaches HasSettings.");

    public string? ActivePaneId => throw new NotSupportedException("No test reaches the active pane.");

    public string? ActiveSessionWorkingDirectory => throw new NotSupportedException("No test reaches the active session.");

    public SessionUsageSnapshot? ActiveSessionUsage => throw new NotSupportedException("No test reaches session usage.");

    public event EventHandler? ActiveSessionChanged
    {
        add { }
        remove { }
    }

    public void AddSettings(Func<Control> createView)
    {
    }

    public void AddSettings(Func<Control> createView, string category)
    {
    }

    public Task ShowSettingsAsync() => Task.CompletedTask;

    public void OnSettingsSaved(Action callback)
    {
    }

    public void AddSideMenuButton(string title, Action onInvoke)
    {
    }

    public SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke) =>
        throw new NotSupportedException("No test reaches a side-menu badge.");

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

    public void AddProviderConfigView(string providerId, Func<string?, IPluginProviderConfigView> createView)
    {
    }

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
        Task.CompletedTask;

    public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
        Task.CompletedTask;

    public Task ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, Action<string>? onStarted = null, Action? onCancelled = null) =>
        Task.CompletedTask;

    public Control CreateMarkdownView(string markdown) => new TextBlock();

    public Control CreateHelpHint(string article, string? section = null, string? label = null) => new Panel();

    public void OpenHelp(string article, string? section = null)
    {
    }

    public bool HasHelp(string article, string? section = null) => false;

    public Task SetClipboardTextAsync(string text) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") => Task.FromResult(false);

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null)
    {
    }

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) =>
        throw new NotSupportedException("No test reaches consent.");

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("No test reaches consent.");

    public Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => Task.FromResult<IReadOnlyList<PluginProfileInfo>>([]);

    public Task SendToSessionAsync(string paneId, string text) => Task.CompletedTask;

    public Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProjectMemoryRow>>([]);

    public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public bool CanSendIntent(string targetPluginId, string action) => false;

    public string? ResolveManagedCliPath(string cliName) => null;

    public Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No test reaches managed CLI status.");

    public Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No test reaches managed CLI installs.");

    public bool RemoveManagedCli(string cliName) => throw new NotSupportedException("No test reaches managed CLI removal.");

    public Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No test reaches MCP auth state.");

    public Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No test reaches MCP sign-in.");
}
