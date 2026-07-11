using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;

namespace Cockpit.App.Services;

/// <summary>
/// Real <see cref="IToastService"/> (#61): marshals onto the UI thread (any caller — a background checker
/// like the #59 plugin-update check runs off the UI thread) and adds the toast to the single
/// <see cref="CockpitViewModel.ToastHost"/>, the same root the overlay in <c>CockpitView.axaml</c> binds to.
/// </summary>
public sealed class ToastService(CockpitViewModel cockpit) : IToastService, ISingletonService
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

    /// <summary>Test seam: the UI-thread logic, driven directly by tests since pumping a real dispatcher loop is not practical (same reasoning as <see cref="VoicePushToTalkCoordinator"/>).</summary>
    internal void ShowOnUiThread(string message, ToastSeverity severity, string? actionLabel, Action? onAction) =>
        cockpit.ToastHost.Add(message, severity, actionLabel, onAction);
}
