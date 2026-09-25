using Avalonia.Controls;
using Cockpit.Plugin.Diagram.Collab;
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

namespace Cockpit.Plugin.Diagram.Tests;

// AC-910 criterion 9: an Ask needs no read/edit consent — SendAsync reaches ICockpitUiHost.SendToSessionAsync
// directly, with no capability gate in between. Locked in here so a later "for tidiness" consent check does not
// slip into the one shared path AskFlyout's callers all send through.
public class SurfaceSessionBindingTests
{
    [Fact]
    public async Task SendAsync_ReachesTheBoundSession_WithNoConsentGateInBetween()
    {
        var host = new _FakeHost();
        var surfaceBinding = new SurfaceSessionBinding(host, null, "pane-1", () => { });

        await surfaceBinding.SendAsync("🗨️ Ask the agent · diagram \"Flow\" (id d1) — explain this");

        Assert.Equal(("pane-1", "🗨️ Ask the agent · diagram \"Flow\" (id d1) — explain this"), host.Sent);
    }

    // F2.13/AC-1401: SurfaceSessionBinding takes ICockpitUiHost now the UI part is its own assembly, and reads a
    // bound pane over the plugin channel (null here — this test cares only about SendAsync, never about liveness).
    // Only SendToSessionAsync does anything; everything else throws, since no test reaches it.
    private sealed class _FakeHost : ICockpitUiHost
    {
        public (string PaneId, string Text)? Sent { get; private set; }

        public Task SendToSessionAsync(string paneId, string text)
        {
            Sent = (paneId, text);
            return Task.CompletedTask;
        }

        public IPluginUiChannel Channel => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public bool HasSettings => throw new NotSupportedException();

        public string? ActivePaneId => null;

        public string? ActiveSessionWorkingDirectory => null;

        public SessionUsageSnapshot? ActiveSessionUsage => null;

        public event EventHandler? ActiveSessionChanged { add { } remove { } }

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
            throw new NotSupportedException();

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

        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) => throw new NotSupportedException();

        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => Task.FromResult<IReadOnlyList<PluginProfileInfo>>([]);

        public Task InsertIntoSessionAsync(string paneId, string text) => Task.CompletedTask;

        public Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProjectMemoryRow>>([]);

        public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public bool CanSendIntent(string targetPluginId, string action) => false;

        public string? ResolveManagedCliPath(string cliName) => null;

        public Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public bool RemoveManagedCli(string cliName) => throw new NotSupportedException();

        public Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
