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
        Toasts.Add(toast);
        item = Notifications.Post(toast, source, key);

        if (!sticky && actions.Count == 0 && severity is ToastSeverity.Information or ToastSeverity.Success)
        {
            _scheduleAutoDismiss(toast, DefaultAutoDismissDelay);
        }

        return toast;
    }

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
