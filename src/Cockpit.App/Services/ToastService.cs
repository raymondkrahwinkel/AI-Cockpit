using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;

namespace Cockpit.App.Services;

// Real `IToastService` (#61): marshals onto the UI thread (any caller — a background checker
// like the #59 plugin-update check runs off the UI thread) and adds the toast to the single
// `CockpitViewModel.ToastHost`, the same root the overlay in `CockpitView.axaml` binds to.
public sealed class ToastService(Func<CockpitViewModel> cockpit) : IToastService, ISingletonService
{
    public void Show(string message, ToastSeverity severity, string? actionLabel = null, Action? onAction = null)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowOnUiThread(message, severity, actionLabel, onAction);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ShowOnUiThread(message, severity, actionLabel, onAction));
        }
    }

    public void Show(string message, ToastSeverity severity, IReadOnlyList<ToastAction> actions, bool sticky) =>
        Show(message, severity, actions, sticky, source: null, key: null);

    public void Show(string message, ToastSeverity severity, IReadOnlyList<ToastAction> actions, bool sticky, string? source, string? key) =>
        Show(message, severity, actions, sticky, source, key, title: null);

    public void Show(string message, ToastSeverity severity, IReadOnlyList<ToastAction> actions, bool sticky, string? source, string? key, string? title)
    {
        if (source == "Swarm" && actions.Count == 0)
        {
            actions = [new ToastAction("Open Swarm", () => cockpit().OpenSwarmOptionsCommand.Execute(null))];
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            cockpit().ToastHost.Add(message, severity, actions, sticky, source, key, title: title);
        }
        else
        {
            Dispatcher.UIThread.Post(() => cockpit().ToastHost.Add(message, severity, actions, sticky, source, key, title: title));
        }
    }

    // Test seam: the UI-thread logic, driven directly by tests since pumping a real dispatcher loop is not practical (same reasoning as `VoicePushToTalkCoordinator`).
    internal void ShowOnUiThread(string message, ToastSeverity severity, string? actionLabel, Action? onAction) =>
        cockpit().ToastHost.Add(message, severity, actionLabel, onAction);
}
