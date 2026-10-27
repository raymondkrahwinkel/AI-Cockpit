using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cockpit.Core.Toasts;

namespace Cockpit.App.ViewModels;

// Toast overlay owner (#61): Add is its UI-thread mutation point and removes close/action/timeout dismissals.
// An injected scheduler makes auto-dismiss deterministic in tests without a dispatcher loop.
public sealed class ToastHostViewModel
{
    private static readonly TimeSpan DefaultAutoDismissDelay = TimeSpan.FromSeconds(5);
    private readonly Action<ToastViewModel, TimeSpan> _scheduleAutoDismiss;
    private List<StartupToast>? _startupToasts;
    private ToastViewModel? _startupBundleToast;
    private Action? _showStartupNotifications;

    public ToastHostViewModel()
        : this(_ScheduleWithDispatcherTimer)
    {
    }

    internal ToastHostViewModel(Action<ToastViewModel, TimeSpan> scheduleAutoDismiss)
    {
        _scheduleAutoDismiss = scheduleAutoDismiss;
    }

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    // AC-1802: every toast also lands here, so one that timed out or was closed can still be read and acted on.
    public NotificationCenterViewModel Notifications { get; } = new();

    public ToastViewModel Add(string message, ToastSeverity severity, string? actionLabel, Action? onAction) =>
        Add(message, severity, !string.IsNullOrWhiteSpace(actionLabel) && onAction is not null ? [new ToastAction(actionLabel, onAction)] : [], sticky: false);

    // AC-1549: several buttons, the first primary; a sticky toast stays until a button or ✕ takes it down.
    public ToastViewModel Add(string message, ToastSeverity severity, IReadOnlyList<ToastAction> actions, bool sticky, string? source = null, string? key = null)
    {
        NotificationItemViewModel? item = null;
        var toast = new ToastViewModel(message, severity, [.. actions.Select(action => new ToastAction(action.Label, () =>
        {
            item?.MarkHandled(action.Label);
            action.OnInvoke();
        }))]);
        toast.Dismissed += (_, _) => Toasts.Remove(toast);
        item = Notifications.Post(toast, source, key);

        if (_startupToasts is not null)
        {
            _startupToasts.Add(new StartupToast(toast, sticky));
            if (_startupToasts.Count >= 2)
            {
                _ShowStartupBundle();
            }

            return toast;
        }

        _Show(toast, sticky);

        return toast;
    }

    // AC-1803: RestoreCockpitAsync starts this short hold before any restore work, so source notices are still
    // immediately live in the center but do not cover the operator in overlays during launch.
    internal void BeginStartupNotificationBundle(TimeSpan duration, Action showNotifications)
    {
        _startupToasts = [];
        _startupBundleToast = null;
        _showStartupNotifications = showNotifications;
        _ = _EndStartupNotificationBundleAsync(duration);
    }

    private async Task _EndStartupNotificationBundleAsync(TimeSpan duration)
    {
        await Task.Delay(duration);
        Dispatcher.UIThread.Post(EndStartupNotificationWindow);
    }

    // The journey ends the window here without waiting on a wall clock; production reaches the same seam from its timer.
    internal void EndStartupNotificationWindow()
    {
        var startupToasts = _startupToasts;
        _startupToasts = null;
        _showStartupNotifications = null;

        if (startupToasts is { Count: < 2 })
        {
            foreach (var startupToast in startupToasts)
            {
                _Show(startupToast.Toast, startupToast.Sticky);
            }
        }
    }

    private void _ShowStartupBundle()
    {
        _startupBundleToast?.CloseCommand.Execute(null);
        var count = _startupToasts!.Count;
        var startupToast = new ToastViewModel(
            $"{count} notifications at startup",
            ToastSeverity.Warning,
            [new ToastAction("Show", () => _showStartupNotifications?.Invoke()), new ToastAction("Later", static () => { })]);
        startupToast.Dismissed += (_, _) => Toasts.Remove(startupToast);
        Toasts.Add(startupToast);
        _startupBundleToast = startupToast;
    }

    private void _Show(ToastViewModel toast, bool sticky)
    {
        Toasts.Add(toast);

        if (!sticky && toast.Actions.Count == 0 && toast.Severity is ToastSeverity.Information or ToastSeverity.Success)
        {
            _scheduleAutoDismiss(toast, DefaultAutoDismissDelay);
        }
    }

    private sealed record StartupToast(ToastViewModel Toast, bool Sticky);

    private static void _ScheduleWithDispatcherTimer(ToastViewModel toast, TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            toast.CloseCommand.Execute(null);
        };
        timer.Start();
    }
}
