extern alias UiAsm;

using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workspaces;
using UiBackend = UiAsm::Cockpit.Plugin.YouTrack.UI.YouTrackBackend;

namespace Cockpit.Plugin.YouTrack.Tests;

// AC-1397: the same fake is also the UI part's host, so one object stands for the cockpit both parts of the plugin
// talk to — the active pane the UI reads is the one the backend's observer holds, and the two meet on Channel as
// they do in the host. Only what the UI part reaches gets a real answer; the rest throws.
internal sealed partial class FakeCockpitHost : ICockpitUiHost
{
    public InProcessChannel Channel { get; } = new();

    // What the UI part sent to a session, pane by pane.
    public List<(string PaneId, string Text)> Sent { get; } = [];

    // What the UI part placed in a session's input without sending — what "Add to prompt" does.
    public List<(string PaneId, string Text)> Inserted { get; } = [];

    IPluginBackendChannel ICockpitHost.Channel => Channel;

    IPluginUiChannel ICockpitUiHost.Channel => Channel;

    public string? ActivePaneId => Observer.ActivePaneId;

    public string? ActiveSessionWorkingDirectory => Observer.ActiveSessionWorkingDirectory;

    public SessionUsageSnapshot? ActiveSessionUsage => null;

    public bool HasSettings => false;

    public event EventHandler? ActiveSessionChanged
    {
        add { }
        remove { }
    }

    // Registers the backend part's real handlers on this host's channel and hands back the UI part's door to them.
    public UiBackend ConnectBackend(SessionIssueLinks links, IssueStateChanges? stateChanges = null)
    {
        new YouTrackChannelHandlers(this, links, stateChanges ?? new IssueStateChanges()).Register(Channel);
        return new UiBackend(Channel);
    }

    public Task SendToSessionAsync(string paneId, string text)
    {
        Sent.Add((paneId, text));
        return Task.CompletedTask;
    }

    public Task InsertIntoSessionAsync(string paneId, string text)
    {
        Inserted.Add((paneId, text));
        return Task.CompletedTask;
    }

    public Task SetClipboardTextAsync(string text) => FakeActions.SetClipboardTextAsync(text);

    public void AddSettings(Func<Control> createView, string category) => throw new NotSupportedException();

    public Task ShowSettingsAsync() => throw new NotSupportedException();

    public void OnSettingsSaved(Action callback) => throw new NotSupportedException();

    public SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke) => throw new NotSupportedException();

    public void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView) => throw new NotSupportedException();

    public void AddSessionBanner(Func<IPluginSessionContext, Control> createView) => throw new NotSupportedException();

    public void AddSessionHeaderAction(PluginSessionAction action) => throw new NotSupportedException();

    public void AddToolbarAction(ToolbarAction action) => throw new NotSupportedException();

    public void AddShortcut(PluginShortcut shortcut) => throw new NotSupportedException();

    public void AddConversationPicker(ConversationPickerRegistration picker) => throw new NotSupportedException();

    public void AddProviderConfigView(string providerId, Func<string?, IPluginProviderConfigView> createView) => throw new NotSupportedException();

    public void AddWidget(WidgetRegistration registration) => throw new NotSupportedException();

    public void AddDockPanel(DockPanelRegistration registration) => throw new NotSupportedException();

    public void AddCompanionTool(CompanionToolRegistration registration) => throw new NotSupportedException();

    public void AddWorkspaceType(WorkspaceTypeRegistration registration) => throw new NotSupportedException();

    public Task OpenWorkspaceAsync(string workspaceTypeId) => throw new NotSupportedException();

    public Control? CreateEmbeddedSessionView(string paneId) => throw new NotSupportedException();

    public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
        throw new NotSupportedException();

    public Control CreateHelpHint(string article, string? section = null, string? label = null) => new Panel();

    public void OpenHelp(string article, string? section = null) => throw new NotSupportedException();

    public bool HasHelp(string article, string? section = null) => false;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") => throw new NotSupportedException();

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) => throw new NotSupportedException();

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => throw new NotSupportedException();

    public Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
        throw new NotSupportedException();

    public bool CanSendIntent(string targetPluginId, string action) => false;

    public string? ResolveManagedCliPath(string cliName) => throw new NotSupportedException();

    public Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public bool RemoveManagedCli(string cliName) => throw new NotSupportedException();

    public Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
