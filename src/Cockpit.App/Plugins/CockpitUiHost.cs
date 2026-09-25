using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
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

// AC-1389 (F2.1): the ICockpitUiHost a plugin's UI part receives. In this step it forwards to the same plugin's
// ICockpitHost, so a registration moved to InitializeUi behaves exactly as it did in Initialize; F2.4 gives it
// its own implementation. `services` stays private: the UI part gets no container to reach around its channel.
internal sealed class CockpitUiHost(string pluginId, ICockpitHost host, IServiceProvider services) : ICockpitUiHost
{
    public IPluginUiChannel Channel { get; } = new UiChannel(services.GetRequiredService<PluginChannelHub>(), pluginId);

    public IPluginStorage Storage => host.Storage;

    public void AddSettings(Func<Control> createView) => host.AddSettings(createView);

    public void AddSettings(Func<Control> createView, string category) => host.AddSettings(createView, category);

    public bool HasSettings => host.HasSettings;

    public Task ShowSettingsAsync() => host.ShowSettingsAsync();

    public void OnSettingsSaved(Action callback) => host.OnSettingsSaved(callback);

    public void AddSideMenuButton(string title, Action onInvoke) => host.AddSideMenuButton(title, onInvoke);

    public SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke) =>
        host.AddSideMenuButtonWithBadge(title, onInvoke);

    public void AddSideMenuSection(string title, Func<Control> createView) => host.AddSideMenuSection(title, createView);

    public void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView) => host.AddSessionHeaderItem(createView);

    public void AddSessionBanner(Func<IPluginSessionContext, Control> createView) => host.AddSessionBanner(createView);

    public void AddSessionHeaderAction(PluginSessionAction action) => host.AddSessionHeaderAction(action);

    public void AddToolbarAction(ToolbarAction action) => host.AddToolbarAction(action);

    public void AddShortcut(PluginShortcut shortcut) => host.AddShortcut(shortcut);

    public void AddConversationPicker(ConversationPickerRegistration picker) => host.AddConversationPicker(picker);

    // ponytail: re-registers the backend's provider with this view, which moves it to the end of the provider list
    // and needs the backend part's AddSessionProvider to have run first (it has: Initialize precedes InitializeUi).
    // Config views need a registry of their own before the flip (F2.14) takes CreateConfigView off the registration.
    public void AddProviderConfigView(string providerId, Func<string?, IPluginProviderConfigView> createView)
    {
        var registry = services.GetRequiredService<IPluginProviderRegistry>();
        if (registry.Resolve(providerId) is not { } registration)
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' registered a config view for provider '{providerId}', which no plugin registered as a session provider.");
        }

        registry.Register(registration with { CreateConfigView = createView });
    }

    public void AddWidget(WidgetRegistration registration) => host.AddWidget(registration);

    public void AddDockPanel(DockPanelRegistration registration) => host.AddDockPanel(registration);

    public void AddCompanionTool(CompanionToolRegistration registration) => host.AddCompanionTool(registration);

    public void AddWorkspaceType(WorkspaceTypeRegistration registration) => host.AddWorkspaceType(registration);

    public Task OpenWorkspaceAsync(string workspaceTypeId) => host.OpenWorkspaceAsync(workspaceTypeId);

    public Control? CreateEmbeddedSessionView(string paneId) =>
        services.GetService<IEmbeddedSessionHost>()?.EmbeddedSessionView(paneId);

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        host.ShowDialogAsync(title, createContent, width, height);

    public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
        host.ShowDialogAsync(title, createContent, singleInstanceKey, width, height);

    public Task ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, Action<string>? onStarted = null, Action? onCancelled = null) =>
        host.ShowNewSessionDialogAsync(prefill, onStarted, onCancelled);

    public Control CreateMarkdownView(string markdown) => host.CreateMarkdownView(markdown);

    public Control CreateHelpHint(string article, string? section = null, string? label = null) =>
        host.CreateHelpHint(article, section, label);

    public void OpenHelp(string article, string? section = null) => host.OpenHelp(article, section);

    public bool HasHelp(string article, string? section = null) => host.HasHelp(article, section);

    public string? ActivePaneId => host.Sessions.ActivePaneId;

    public string? ActiveSessionWorkingDirectory => host.Sessions.ActiveSessionWorkingDirectory;

    public SessionUsageSnapshot? ActiveSessionUsage => host.Sessions.ActiveSessionUsage;

    public event EventHandler? ActiveSessionChanged
    {
        add => host.Sessions.ActiveSessionChanged += value;
        remove => host.Sessions.ActiveSessionChanged -= value;
    }

    public Task SetClipboardTextAsync(string text) => host.Actions.SetClipboardTextAsync(text);

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") =>
        host.Actions.ConfirmAsync(title, message, confirmLabel);

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        host.ShowToast(message, severity, actionLabel, onAction);

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) => host.RequestConsentAsync(request);

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) =>
        host.RequestConsentAsync(request, cancellationToken);

    public Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => host.GetProfilesAsync();

    public Task SendToSessionAsync(string paneId, string text) => host.SendToSessionAsync(paneId, text);

    public Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
        host.GetProjectMemoryRowsAsync(paneId, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
        host.SendIntent(targetPluginId, action, data);

    public bool CanSendIntent(string targetPluginId, string action) => host.CanSendIntent(targetPluginId, action);

    public string? ResolveManagedCliPath(string cliName) => host.ResolveManagedCliPath(cliName);

    public Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) =>
        host.GetManagedCliStatusAsync(cliName, cancellationToken);

    public Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) =>
        host.InstallManagedCliAsync(cliName, cancellationToken);

    public bool RemoveManagedCli(string cliName) => host.RemoveManagedCli(cliName);

    public Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) =>
        host.GetManagedCliAutoUpdateAsync(cliName, cancellationToken);

    public Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) =>
        host.SetManagedCliAutoUpdateAsync(cliName, enabled, cancellationToken);

    public Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) =>
        host.GetMcpServerAuthStateAsync(name, cancellationToken);

    public Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        host.SignInMcpServerAsync(name, cancellationToken);

    private sealed class UiChannel(PluginChannelHub hub, string pluginId) : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            hub.InvokeAsync(pluginId, action, payload, cancellationToken);

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) =>
            hub.Subscribe(pluginId, name, handler);
    }
}
