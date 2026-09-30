using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Cockpit.App.Controls;
using Cockpit.App.Docking;
using Cockpit.App.Services;
using Cockpit.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
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

// AC-1389: the ICockpitUiHost a plugin's UI part receives. The window members are its own; what a backend can do
// too (consent, intents, managed CLIs, ...) forwards to the same plugin's ICockpitHost. `services` stays private,
// so the UI part gets no container to reach around its channel.
internal sealed class CockpitUiHost(
    string pluginId,
    string pluginName,
    ICockpitHost host,
    IServiceProvider services,
    IPluginContributionSink contributionSink,
    IPluginDialogHost dialogHost,
    IPluginWindowActions actions,
    IPluginActiveSession activeSession,
    IReadOnlyList<string> declaredSecretKeys) : ICockpitUiHost
{
    public IPluginUiChannel Channel { get; } = new UiChannel(services.GetRequiredService<PluginChannelHub>(), pluginId);

    public IPluginStorage Storage => host.Storage;

    public void AddSettings(Func<Control> createView) =>
        contributionSink.AddPluginSettings(pluginId, pluginName, createView);

    public void AddSettings(Func<Control> createView, string category) =>
        contributionSink.AddPluginSettings(pluginId, pluginName, createView, category);

    public bool HasSettings => contributionSink.HasPluginSettings(pluginId);

    public Task ShowSettingsAsync() => contributionSink.OpenPluginSettingsAsync(pluginId);

    public void OnSettingsSaved(Action callback) =>
        contributionSink.AddSettingsSavedHandler(pluginId, callback);

    public void AddSideMenuButton(string title, Action onInvoke) =>
        contributionSink.AddPluginSideButton(pluginId, title, onInvoke);

    public SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke)
    {
        var badge = new SideMenuButtonBadge();
        contributionSink.AddPluginSideButton(pluginId, title, onInvoke, badge);
        return badge;
    }

    public void AddSideMenuSection(string title, Func<Control> createView) =>
        contributionSink.AddPluginSideSection(pluginId, title, createView);

    public void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView) =>
        contributionSink.AddPluginSessionHeaderItem(createView);

    public void AddSessionBanner(Func<IPluginSessionContext, Control> createView) =>
        contributionSink.AddPluginSessionBannerItem(createView);

    public void AddSessionHeaderAction(PluginSessionAction action) =>
        contributionSink.AddPluginSessionHeaderAction(action);

    public void AddToolbarAction(ToolbarAction action) =>
        contributionSink.AddToolbarAction(pluginId, action);

    public void AddShortcut(PluginShortcut shortcut) =>
        contributionSink.AddPluginShortcut(shortcut);

    public void AddConversationPicker(ConversationPickerRegistration picker) =>
        services.GetRequiredService<IConversationPickerRegistry>().Register(picker);

    public void AddProviderConfigView(string providerId, Func<string?, IPluginProviderConfigView> createView)
    {
        if (services.GetRequiredService<IPluginProviderRegistry>().Resolve(providerId) is null)
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' registered a config view for provider '{providerId}', which no plugin registered as a session provider.");
        }

        services.GetRequiredService<IPluginProviderConfigViews>().Register(providerId, createView);
    }

    // This plugin's own storage, observe surface and declared secret keys travel with the registration: a placed
    // instance builds its context long after load, and by then the widget id is the only thing linking it back
    // here. The declared keys are what lets an export drop a credential the name rule cannot guess ("pat").
    public void AddWidget(WidgetRegistration registration)
    {
        // Refused means another plugin already contributes this type id. Logged rather than thrown: a plugin
        // cannot know what else is installed, and taking the cockpit down over a name clash is a worse answer
        // than the widget being the one that was already there.
        if (!services.GetRequiredService<IWidgetRegistry>().Register(registration, host.Storage, host.Sessions, declaredSecretKeys))
        {
            _Logger()?.LogWarning("Widget type '{WidgetId}' is already contributed by another plugin; this registration is ignored", registration.Id);
        }
    }

    // Unlike AddWidget, no storage/sessions travel with this registration: a dock panel's view factory takes no
    // context, so a plugin that needs per-instance state builds its own IWidgetContext from what host.Storage and
    // host.Sessions already give it.
    public void AddDockPanel(DockPanelRegistration registration)
    {
        if (!services.GetRequiredService<IDockPanelRegistry>().Register(registration))
        {
            _Logger()?.LogWarning("Dock panel '{DockPanelId}' is already contributed by another plugin; this registration is ignored", registration.Id);
        }
    }

    // This plugin's own storage and observe surface travel with the registration, the same way a widget's do: a
    // tool's context is built long after load, and by then the tool id is the only thing linking it back here.
    public void AddCompanionTool(CompanionToolRegistration registration)
    {
        if (!services.GetRequiredService<ICompanionToolRegistry>().Register(registration, host.Storage, host.Sessions))
        {
            _Logger()?.LogWarning("Companion tool '{CompanionToolId}' is already contributed by another plugin; this registration is ignored", registration.Id);
        }
    }

    // The same reasons as AddWidget: the type id is the only link back here once a workspace of it is built, and a
    // clash is logged rather than thrown.
    public void AddWorkspaceType(WorkspaceTypeRegistration registration)
    {
        if (!services.GetRequiredService<IWorkspaceTypeRegistry>().Register(registration, host.Storage, host.Sessions))
        {
            _Logger()?.LogWarning("Workspace type '{WorkspaceTypeId}' is already contributed by another plugin; this registration is ignored", registration.Id);
        }
    }

    // The programmatic "+" for a plugin's own workspace type: a plugin that received an intent surfaces its
    // workspace so the operator lands on it. Marshalled to the UI thread since a plugin may dispatch from any
    // thread; a design-time/headless host (no view model resolved) simply does nothing.
    public async Task OpenWorkspaceAsync(string workspaceTypeId)
    {
        // The plugin's workspaces live on the on-screen view model — the same instance the UI binds to — not a
        // separately DI-resolved WorkspacesViewModel, which would open the workspace on a surface no one is looking at.
        if (services.GetService<CockpitViewModel>()?.Workspaces is not { } workspaces)
        {
            return;
        }

        // AC-577: no CheckAccess() fast path, deliberately — that shortcut would let a test pass inline
        // without proving anything about marshalling; a test for this belongs in Cockpit.App.ViewTests.
        try
        {
            await UiThreadCall.DispatchAsync(() => workspaces.OpenWorkspaceAsync(workspaceTypeId));
        }
        catch (UiUnavailableException exception)
        {
            // AC-1138: logged and passed on, not swallowed. A plugin that starts this from a menu button
            // discards the task, and then the workspace not opening is all the operator has to go on — the
            // reason would be nowhere. An awaiting caller still gets the exception.
            _Logger()?.LogWarning(exception, "Opening workspace '{WorkspaceTypeId}' for plugin '{PluginId}' gave up waiting for the UI thread", workspaceTypeId, pluginId);
            throw;
        }
    }

    public Control? CreateEmbeddedSessionView(string paneId) =>
        services.GetService<IEmbeddedSessionHost>()?.EmbeddedSessionView(paneId);

    // A plugin's dialog gets a gear in its title bar when the plugin has settings to open — checked when the
    // dialog opens, not when the plugin is built, since settings and dialogs can register in any order.
    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        _ShowPluginDialogAsync(title, createContent, width, height, singleInstanceKey: null);

    public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
        // Scoped to the plugin, so a plugin only has to be unique within itself: two plugins picking "issues"
        // are two windows, which is what they are. Without the scope the first plugin to open one would answer
        // for the other, and the operator would act on the wrong repository.
        _ShowPluginDialogAsync(title, createContent, width, height, $"{pluginId}:{singleInstanceKey}");

    private Task _ShowPluginDialogAsync(string title, Func<Control> createContent, double width, double height, string? singleInstanceKey) =>
        dialogHost.ShowDialogAsync(
            title,
            createContent,
            width,
            height,
            onOpenSettings: contributionSink.HasPluginSettings(pluginId)
                ? () => contributionSink.OpenPluginSettingsAsync(pluginId)
                : null,
            singleInstanceKey: singleInstanceKey);

    // Opens the New-session dialog (#AC-96) pre-filled from `prefill`; exactly one callback runs, `onStarted`
    // or `onCancelled`. Routed through `CockpitViewModel` so the session is minted by the app's own launch
    // path (worktree isolation, Duplicate's launch-result), not a second, divergent one.
    public async Task ShowNewSessionDialogAsync(
        NewSessionPrefill? prefill = null,
        Action<string>? onStarted = null,
        Action? onCancelled = null)
    {
        string? paneId;
        try
        {
            // AC-577, no fast path — deliberately, and here it is not even a trade: what this marshals to is a
            // modal dialog, which has nowhere to appear in a process without a dispatcher loop. An inline branch
            // would turn "no UI thread" from a hang into a dialog nobody can answer.
            var dialog = await UiThreadCall.DispatchAsync<Task<string?>>(() =>
                services.GetService<CockpitViewModel>() is { } cockpit
                    ? cockpit.ShowNewSessionDialogForPluginAsync(prefill)
                    : Task.FromResult<string?>(null));

            // AC-1138: the explicit type argument caps the hop onto the thread and hands the dialog's own task
            // back unopened — what comes after is an operator reading a dialog, not a wait to cap. The
            // unwrapping overload would have capped both.
            paneId = await dialog;
        }
        catch (Exception ex)
        {
            // The exactly-one-callback contract has to hold even when the dialog or the launch throws: a plugin that
            // bridges these callbacks to a TaskCompletionSource would otherwise wait forever. A failure is "nothing
            // started" — log it and fall through to onCancelled rather than letting the exception drop both callbacks.
            _Logger()?.LogError(ex, "Opening the New-session dialog for plugin '{PluginId}' failed", pluginId);
            paneId = null;
        }

        if (paneId is not null)
        {
            onStarted?.Invoke(paneId);
        }
        else
        {
            onCancelled?.Invoke();
        }
    }

    // Delegates to the cockpit's own `MarkdownView` — no second parser, one markdown idiom for both the transcript and every plugin dialog.
    public Control CreateMarkdownView(string markdown) => new MarkdownView { Markdown = _CapForRendering(markdown) };

    // AC-303: caps a plugin-supplied markdown body (e.g. a 65 KB GitHub issue) before synchronous rendering,
    // which otherwise stalls the UI in an all-Auto grid; capped here, not in MarkdownView, since the transcript
    // also renders through that control and must not be truncated.
    private const int MaxPluginMarkdownCharacters = 64 * 1024;

    // Nullable although the contract says otherwise: the caller is plugin code, which may well be compiled with
    // nullable disabled, and passing null here used to render an empty view rather than throw. Dereferencing it
    // would move that into an exception on the host's UI thread — the very shape AC-304 is about.
    private static string? _CapForRendering(string? markdown) =>
        markdown is { Length: > MaxPluginMarkdownCharacters }
            ? string.Concat(
                markdown.AsSpan(0, MaxPluginMarkdownCharacters),
                "\n\n*(truncated — open the item in its tracker to read the rest)*")
            : markdown;

    // AC-1033. `article` is resolved against this plugin's own branch first, then as written, so a plugin
    // names its own page with the id it gave the file and can still point at one of ours — without repeating
    // its own id, which would be a second place its name is written down.
    public Control CreateHelpHint(string article, string? section = null, string? label = null) =>
        _Help() is { } help
            ? new HelpHint(help, help.Resolve(pluginId, article, section), label, $"a “?” in {pluginName}")
            : new Panel { IsVisible = false };

    public void OpenHelp(string article, string? section = null)
    {
        if (_Help() is { } help)
        {
            help.Open(help.Resolve(pluginId, article, section), $"a link in {pluginName}");
        }
    }

    public bool HasHelp(string article, string? section = null) =>
        _Help() is { } help && help.Contains(help.Resolve(pluginId, article, section));

    public string? ActivePaneId => activeSession.ActivePaneId;

    public string? ActiveSessionWorkingDirectory => activeSession.ActiveSessionWorkingDirectory;

    public SessionUsageSnapshot? ActiveSessionUsage => activeSession.ActiveSessionUsage;

    public event EventHandler? ActiveSessionChanged
    {
        add => activeSession.ActiveSessionChanged += value;
        remove => activeSession.ActiveSessionChanged -= value;
    }

    public event EventHandler? ActiveSessionUsageChanged
    {
        add => activeSession.ActiveSessionUsageChanged += value;
        remove => activeSession.ActiveSessionUsageChanged -= value;
    }

    public Task SetClipboardTextAsync(string text) => actions.SetClipboardTextAsync(text);

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") =>
        actions.ConfirmAsync(title, message, confirmLabel);

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        host.ShowToast(message, severity, actionLabel, onAction);

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) => host.RequestConsentAsync(request);

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) =>
        host.RequestConsentAsync(request, cancellationToken);

    public Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => host.GetProfilesAsync();

    public Task SendToSessionAsync(string paneId, string text) => host.SendToSessionAsync(paneId, text);

    // AC-1397: what PluginActions.InjectIntoActiveSessionAsync does for the selected pane, for a named one — placed, not sent.
    public Task InsertIntoSessionAsync(string paneId, string text)
    {
        var cockpit = services.GetService<CockpitViewModel>();
        cockpit?.Sessions.Append<SessionPanelViewModel?>(cockpit.AssistantPane)
            .FirstOrDefault(session => session is not null && string.Equals(session.PaneId, paneId, StringComparison.Ordinal))
            ?.InjectText(text);
        return Task.CompletedTask;
    }

    // No pane named means this window's selected one, which only a UI part has.
    public Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
        host.GetProjectMemoryRowsAsync(string.IsNullOrEmpty(paneId) ? activeSession.ActivePaneId : paneId, cancellationToken);

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

    private ILogger? _Logger() => services.GetService<ILoggerFactory>()?.CreateLogger<CockpitUiHost>();

    private HelpService? _Help() => services.GetService<HelpService>();

    private sealed class UiChannel(PluginChannelHub hub, string pluginId) : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            hub.InvokeAsync(pluginId, action, payload, cancellationToken);

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) =>
            hub.Subscribe(pluginId, name, handler);
    }
}
