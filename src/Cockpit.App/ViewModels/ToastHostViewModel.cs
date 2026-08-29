using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cockpit.Core.Toasts;

namespace Cockpit.App.ViewModels;

// Toast overlay owner (#61): Add is its UI-thread mutation point and removes close/action/timeout dismissals.
// An injected scheduler makes auto-dismiss deterministic in tests without a dispatcher loop.
public sealed class ToastHostViewModel
{
    private static readonly TimeSpan DefaultAutoDismissDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorAutoDismissDelay = TimeSpan.FromSeconds(8);

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

    // Builds and shows one toast, auto-dismissing after a severity-dependent delay (longer for `ToastSeverity.Error`).
    public ToastViewModel Add(string message, ToastSeverity severity, string? actionLabel, Action? onAction)
    {
        var toast = new ToastViewModel(message, severity, actionLabel, onAction);
        toast.Dismissed += (_, _) => Toasts.Remove(toast);
        Toasts.Add(toast);

        var delay = severity == ToastSeverity.Error ? ErrorAutoDismissDelay : DefaultAutoDismissDelay;
        _scheduleAutoDismiss(toast, delay);

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
