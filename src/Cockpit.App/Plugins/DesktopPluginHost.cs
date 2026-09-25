using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Controls;
using Cockpit.App.Docking;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

// AC-1392: the `ICockpitHost` a plugin's backend part receives on the desktop — PluginBackendHost for everything a
// backend can do, and the window members on top, because a plugin still registers its UI in Initialize until the flip.
// ponytail: a bridge; F2.14 (AC-1369) removes it, and CockpitUiHost then carries these window members itself.
internal sealed class DesktopPluginHost(
    string pluginId,
    string pluginName,
    IServiceProvider services,
    IPluginContributionSink contributionSink,
    ICockpitActions actions,
    IPluginStorage storage,
    IPluginDialogHost dialogHost,
    ICockpitSessionObserver sessions,
    PluginDiagnostics diagnostics,
    IReadOnlyList<string>? declaredSecretKeys = null,
    Type? ownPluginType = null,
    IPluginCache? cache = null)
    : PluginBackendHost(pluginId, pluginName, services, storage, sessions, actions, diagnostics, ownPluginType, cache), ICockpitHost
{
    protected override IReadOnlyList<string> LegacyClaudeModels => SessionOptionCatalog.ClaudeModelSuggestions;

    // AC-1342: the native/legacy-typed Claude profile's own effort levels — SessionOptionCatalog.Efforts mirrors
    // ClaudeOptionChoices.EffortLevels, so a profile still on the pre-plugin typed config reports the same levels either way.
    protected override IReadOnlyList<string> LegacyClaudeEfforts { get; } = [.. SessionOptionCatalog.Efforts.Select(effort => effort.Value)];

    // AC-1379: the gateway keeps no thread of its own; this host makes the UI-thread hops it used to make itself.
    protected override IAssistantSessionHost AssistantHostFor(IAssistantSessionHost host) => new UiThreadAssistantSessionHost(host);

    public void AddSettings(Func<Control> createView) =>
        contributionSink.AddPluginSettings(PluginId, PluginName, createView);

    public void AddSettings(Func<Control> createView, string category) =>
        contributionSink.AddPluginSettings(PluginId, PluginName, createView, category);

    public override bool HasSettings => contributionSink.HasPluginSettings(PluginId);

    public override Task ShowSettingsAsync() => contributionSink.OpenPluginSettingsAsync(PluginId);

    public override void OnSettingsSaved(Action callback) =>
        contributionSink.AddSettingsSavedHandler(PluginId, callback);

    public override void AddSideMenuButton(string title, Action onInvoke) =>
        contributionSink.AddPluginSideButton(PluginId, title, onInvoke);

    public override SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke)
    {
        var badge = new SideMenuButtonBadge();
        contributionSink.AddPluginSideButton(PluginId, title, onInvoke, badge);
        return badge;
    }

    public override void AddShortcut(PluginShortcut shortcut) =>
        contributionSink.AddPluginShortcut(shortcut);

    public override void ShowToast(string message, PluginToastSeverity severity, string? actionLabel, Action? onAction)
    {
        // AC-1074: a toast is gone in seconds, and an error nobody was looking at is exactly what the log is for.
        if (severity is PluginToastSeverity.Error)
        {
            Services.GetService<ILogger<DesktopPluginHost>>()?.LogError("Plugin {PluginId}: {Message}", PluginId, message);
        }

        Services.GetRequiredService<IToastService>().Show(message, _ToToastSeverity(severity), actionLabel, onAction);
    }

    public void AddSideMenuSection(string title, Func<Control> createView) =>
        contributionSink.AddPluginSideSection(PluginId, title, createView);

    public override void AddSessionHeaderAction(PluginSessionAction action) =>
        contributionSink.AddPluginSessionHeaderAction(action);

    public void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView) =>
        contributionSink.AddPluginSessionHeaderItem(createView);

    public void AddSessionBanner(Func<IPluginSessionContext, Control> createView) =>
        contributionSink.AddPluginSessionBannerItem(createView);

    public override void AddSupervisedActivityProvider(ISupervisedActivitySource source) =>
        contributionSink.AddSupervisedActivityProvider(source);

    public override void AddToolbarAction(ToolbarAction action) =>
        contributionSink.AddToolbarAction(PluginId, action);

    // This plugin's own storage, observe surface and declared secret keys travel with the registration: a placed
    // instance builds its context long after load, and by then the widget id is the only thing linking it back
    // here. The declared keys are what lets an export drop a credential the name rule cannot guess ("pat").
    public override void AddWidget(WidgetRegistration registration)
    {
        // Refused means another plugin already contributes this type id. Logged rather than thrown: a plugin
        // cannot know what else is installed, and taking the cockpit down over a name clash is a worse answer
        // than the widget being the one that was already there.
        if (!Services.GetRequiredService<IWidgetRegistry>().Register(registration, Storage, Sessions, declaredSecretKeys ?? []))
        {
            _Logger()?.LogWarning("Widget type '{WidgetId}' is already contributed by another plugin; this registration is ignored", registration.Id);
        }
    }

    public override IReadOnlyList<WidgetRegistration> Widgets =>
        Services.GetRequiredService<IWidgetRegistry>().Widgets;

    // Unlike AddWidget, no storage/sessions travel with this registration: a dock panel's view factory takes no
    // context, so a plugin that needs per-instance state builds its own IWidgetContext from what host.Storage and
    // host.Sessions already give it.
    public override void AddDockPanel(DockPanelRegistration registration)
    {
        if (!Services.GetRequiredService<IDockPanelRegistry>().Register(registration))
        {
            _Logger()?.LogWarning("Dock panel '{DockPanelId}' is already contributed by another plugin; this registration is ignored", registration.Id);
        }
    }

    // This plugin's own storage and observe surface travel with the registration, the same way a widget's do: a
    // tool's context is built long after load, and by then the tool id is the only thing linking it back here.
    public override void AddCompanionTool(CompanionToolRegistration registration)
    {
        if (!Services.GetRequiredService<ICompanionToolRegistry>().Register(registration, Storage, Sessions))
        {
            _Logger()?.LogWarning("Companion tool '{CompanionToolId}' is already contributed by another plugin; this registration is ignored", registration.Id);
        }
    }

    public override IReadOnlyList<CompanionToolRegistration> CompanionTools =>
        Services.GetRequiredService<ICompanionToolRegistry>().Tools;

    // The same reasons as AddWidget: the type id is the only link back here once a workspace of it is built, and a
    // clash is logged rather than thrown.
    public override void AddWorkspaceType(WorkspaceTypeRegistration registration)
    {
        if (!Services.GetRequiredService<IWorkspaceTypeRegistry>().Register(registration, Storage, Sessions))
        {
            _Logger()?.LogWarning("Workspace type '{WorkspaceTypeId}' is already contributed by another plugin; this registration is ignored", registration.Id);
        }
    }

    public override IReadOnlyList<WorkspaceTypeRegistration> WorkspaceTypes =>
        Services.GetRequiredService<IWorkspaceTypeRegistry>().WorkspaceTypes;

    // AC-1398: the route WorkspaceContext.EmbedSession takes, reachable by workspace id so a backend part can embed
    // what its UI part's workspace shows. Unmarshalled like that route: the caller is on the UI thread already.
    public IEmbeddedSession? EmbedSession(string workspaceId, EmbeddedSessionRequest request) =>
        Services.GetService<IEmbeddedSessionHost>()?.Embed(workspaceId, request);

    // The programmatic "+" for a plugin's own workspace type: a plugin that received an intent surfaces its
    // workspace so the operator lands on it. Marshalled to the UI thread since a plugin may dispatch from any
    // thread; a design-time/headless host (no view model resolved) simply does nothing.
    public override async Task OpenWorkspaceAsync(string workspaceTypeId)
    {
        // The plugin's workspaces live on the on-screen view model — the same instance the UI binds to — not a
        // separately DI-resolved WorkspacesViewModel, which would open the workspace on a surface no one is looking at.
        if (Services.GetService<CockpitViewModel>()?.Workspaces is not { } workspaces)
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
            _Logger()?.LogWarning(exception, "Opening workspace '{WorkspaceTypeId}' for plugin '{PluginId}' gave up waiting for the UI thread", workspaceTypeId, PluginId);
            throw;
        }
    }

    // A plugin's dialog gets a gear in its title bar when the plugin has settings to open — checked when the
    // dialog opens, not when the plugin is built, since settings and dialogs can register in any order.
    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        _ShowPluginDialogAsync(title, createContent, width, height, singleInstanceKey: null);

    public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
        // Scoped to the plugin, so a plugin only has to be unique within itself: two plugins picking "issues"
        // are two windows, which is what they are. Without the scope the first plugin to open one would answer
        // for the other, and the operator would act on the wrong repository.
        _ShowPluginDialogAsync(title, createContent, width, height, $"{PluginId}:{singleInstanceKey}");

    private Task _ShowPluginDialogAsync(string title, Func<Control> createContent, double width, double height, string? singleInstanceKey) =>
        dialogHost.ShowDialogAsync(
            title,
            createContent,
            width,
            height,
            onOpenSettings: contributionSink.HasPluginSettings(PluginId)
                ? () => contributionSink.OpenPluginSettingsAsync(PluginId)
                : null,
            singleInstanceKey: singleInstanceKey);

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

    // Opens the New-session dialog (#AC-96) pre-filled from `prefill`; exactly one callback runs, `onStarted`
    // or `onCancelled`. Routed through `CockpitViewModel` so the session is minted by the app's own launch
    // path (worktree isolation, Duplicate's launch-result), not a second, divergent one.
    public override async Task ShowNewSessionDialogAsync(
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
                Services.GetService<CockpitViewModel>() is { } cockpit
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
            _Logger()?.LogError(ex, "Opening the New-session dialog for plugin '{PluginId}' failed", PluginId);
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

    // AC-1033. `article` is resolved against this plugin's own branch first, then as written, so a plugin
    // names its own page with the id it gave the file and can still point at one of ours — without repeating
    // its own id, which would be a second place its name is written down.
    public Control CreateHelpHint(string article, string? section = null, string? label = null) =>
        _Help() is { } help
            ? new HelpHint(help, help.Resolve(PluginId, article, section), label, $"a “?” in {PluginName}")
            : new Panel { IsVisible = false };

    public override void OpenHelp(string article, string? section = null)
    {
        if (_Help() is { } help)
        {
            help.Open(help.Resolve(PluginId, article, section), $"a link in {PluginName}");
        }
    }

    public override bool HasHelp(string article, string? section = null) =>
        _Help() is { } help && help.Contains(help.Resolve(PluginId, article, section));

    private HelpService? _Help() => Services.GetService<HelpService>();

    private ILogger? _Logger() => Services.GetService<ILoggerFactory>()?.CreateLogger<DesktopPluginHost>();

    // Maps by name, not ordinal.
    private static ToastSeverity _ToToastSeverity(PluginToastSeverity severity) => severity switch
    {
        PluginToastSeverity.Success => ToastSeverity.Success,
        PluginToastSeverity.Warning => ToastSeverity.Warning,
        PluginToastSeverity.Error => ToastSeverity.Error,
        _ => ToastSeverity.Information,
    };
}
