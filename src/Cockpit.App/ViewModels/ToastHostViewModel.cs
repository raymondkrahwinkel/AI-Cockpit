using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cockpit.Core.Toasts;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Owns the live toast collection <see cref="CockpitViewModel"/> exposes to <c>CockpitView.axaml</c>'s
/// overlay (#61). <see cref="Add"/> is the single mutation point: it builds the <see cref="ToastViewModel"/>,
/// wires its dismissal (close button, action button, or auto-dismiss) back to removal, and schedules the
/// auto-dismiss itself. Callers must already be on the UI thread — <see cref="Services.ToastService"/> does
/// that marshaling before calling in.
/// </summary>
/// <remarks>
/// The auto-dismiss scheduler is an injectable delegate (defaulting to a real <see cref="DispatcherTimer"/>)
/// so tests can simulate "the timeout elapsed" deterministically instead of waiting on real wall-clock time
/// or pumping an Avalonia dispatcher loop — same seam style as <see cref="Services.AppRestartService"/>.
/// </remarks>
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

    /// <summary>Builds and shows one toast, auto-dismissing after a severity-dependent delay (longer for <see cref="ToastSeverity.Error"/>).</summary>
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
