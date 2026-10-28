using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

// AC-1392: the `ICockpitHost` a plugin's backend part receives on the desktop — PluginBackendHost for everything a
// backend can do, plus what only the desktop's own stack answers: the legacy Claude model and effort lists, the
// assistant's UI-thread hop, real toasts and the embedded-session route. The window members are CockpitUiHost's.
internal sealed class DesktopBackendHost(
    string pluginId,
    string pluginName,
    IServiceProvider services,
    ICockpitActions actions,
    IPluginStorage storage,
    ICockpitSessionObserver sessions,
    PluginDiagnostics diagnostics,
    Type? ownPluginType = null,
    IPluginCache? cache = null,
    IReadOnlyList<string>? capabilities = null)
    : PluginBackendHost(pluginId, pluginName, services, storage, sessions, actions, diagnostics, ownPluginType, cache, capabilities), ICockpitHost
{
    protected override IReadOnlyList<string> LegacyClaudeModels => SessionOptionCatalog.ClaudeModelSuggestions;

    // AC-1342: the native/legacy-typed Claude profile's own effort levels — SessionOptionCatalog.Efforts mirrors
    // ClaudeOptionChoices.EffortLevels, so a profile still on the pre-plugin typed config reports the same levels either way.
    protected override IReadOnlyList<string> LegacyClaudeEfforts { get; } = [.. SessionOptionCatalog.Efforts.Select(effort => effort.Value)];

    // AC-1379: the gateway keeps no thread of its own; this host makes the UI-thread hops it used to make itself.
    protected override IAssistantSessionHost AssistantHostFor(IAssistantSessionHost host) => new UiThreadAssistantSessionHost(host);

    public override void ShowToast(string message, PluginToastSeverity severity, string? actionLabel, Action? onAction)
    {
        // AC-1074: a toast is gone in seconds, and an error nobody was looking at is exactly what the log is for.
        if (severity is PluginToastSeverity.Error)
        {
            Services.GetService<ILogger<DesktopBackendHost>>()?.LogError("Plugin {PluginId}: {Message}", PluginId, message);
        }

        // AC-1853: under this plugin's id too, so the notification center finds the plugin's icon.
        var actions = !string.IsNullOrWhiteSpace(actionLabel) && onAction is not null ? [new ToastAction(actionLabel, onAction)] : Array.Empty<ToastAction>();
        _Post(toasts => toasts.Add(message, ToToastSeverity(severity), actions, sticky: false, PluginName, sourceId: PluginId),
            () => Services.GetRequiredService<IToastService>().Show(message, ToToastSeverity(severity), actions, sticky: false, source: PluginName, key: null));
    }

    // AC-1549: a reminder's toast, with all its buttons, also while the plugin's own screen is closed.
    public override void ShowToast(string message, PluginToastSeverity severity, IReadOnlyList<PluginToastAction> actions, bool sticky)
    {
        ToastAction[] toastActions = [.. actions.Select(action => new ToastAction(action.Label, action.OnInvoke))];
        _Post(toasts => toasts.Add(message, ToToastSeverity(severity), toastActions, sticky, PluginName, sourceId: PluginId),
            () => Services.GetRequiredService<IToastService>().Show(message, ToToastSeverity(severity), toastActions, sticky, PluginName, key: null));
    }

    // AC-1804: kept under this plugin's id, so the restart after it asks this plugin again.
    public void Notify(PluginNotification notification) =>
        _Post(toasts => toasts.Notify(PluginId, PluginName, notification),
            () => ShowToast(notification.Message, notification.Severity, notification.Actions, sticky: notification.Actions.Count > 0));

    // Straight onto the desktop's toast host on its UI thread; without one (a test host) through the toast service.
    private void _Post(Action<ToastHostViewModel> post, Action fallback)
    {
        if (Services.GetService<CockpitViewModel>()?.ToastHost is not { } toasts)
        {
            fallback();
        }
        else if (Dispatcher.UIThread.CheckAccess())
        {
            post(toasts);
        }
        else
        {
            Dispatcher.UIThread.Post(() => post(toasts));
        }
    }

    public void AddNotificationSource(Func<string, Task<PluginNotification?>> recheck) =>
        Services.GetService<CockpitViewModel>()?.ToastHost.AddNotificationSource(PluginId, recheck);

    // AC-1398: the route WorkspaceContext.EmbedSession takes, reachable by workspace id so a backend part can embed
    // what its UI part's workspace shows. AC-1418: marshals itself, since a backend part has no UI thread to be on.
    public IEmbeddedSession? EmbedSession(string workspaceId, EmbeddedSessionRequest request)
    {
        if (Services.GetService<IEmbeddedSessionHost>() is not { } host)
        {
            return null;
        }

        return Dispatcher.UIThread.CheckAccess()
            ? host.Embed(workspaceId, request)
            : Dispatcher.UIThread.Invoke(() => host.Embed(workspaceId, request));
    }

    // Maps by name, not ordinal.
    internal static ToastSeverity ToToastSeverity(PluginToastSeverity severity) => severity switch
    {
        PluginToastSeverity.Success => ToastSeverity.Success,
        PluginToastSeverity.Warning => ToastSeverity.Warning,
        PluginToastSeverity.Error => ToastSeverity.Error,
        _ => ToastSeverity.Information,
    };
}
