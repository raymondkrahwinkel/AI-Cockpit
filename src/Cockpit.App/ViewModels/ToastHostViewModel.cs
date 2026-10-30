using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.App.ViewModels;

// Toast overlay owner (#61): Add is its UI-thread mutation point and removes close/action/timeout dismissals.
// An injected scheduler makes auto-dismiss deterministic in tests without a dispatcher loop.
public sealed class ToastHostViewModel
{
    public TimeSpan AutoDismissDelay { get; set; } = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, ConditionToast> _conditions = new(StringComparer.Ordinal);
    private readonly bool _automaticallyRecheck;
    private DispatcherTimer? _conditionTimer;
    private const int MaxBundleLines = 3;
    private readonly Action<ToastViewModel, TimeSpan> _scheduleAutoDismiss;
    private List<StartupToast>? _startupToasts;
    private ToastViewModel? _startupBundleToast;
    private Action? _showStartupNotifications;
    private readonly ConcurrentDictionary<string, Func<string, Task<PluginNotification?>>> _sources = new();

    public ToastHostViewModel()
        : this(_ScheduleWithDispatcherTimer)
    {
        _automaticallyRecheck = true;
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
    public ToastViewModel Add(string message, ToastSeverity severity, IReadOnlyList<ToastAction> actions, bool sticky, string? source = null, string? key = null, string? sourceId = null, string? title = null)
    {
        NotificationItemViewModel? item = null;
        var toast = new ToastViewModel(message, severity, [.. actions.Select(action => new ToastAction(action.Label, () =>
        {
            item?.MarkHandled(action.Label);
            action.OnInvoke();
        }))])
        {
            Title = title,
        };
        toast.Dismissed += (_, _) =>
        {
            Toasts.Remove(toast);
            _startupToasts?.RemoveAll(queued => ReferenceEquals(queued.Toast, toast));
        };
        item = Notifications.Post(toast, source, key, sourceId);

        if (_startupToasts is not null)
        {
            _startupToasts.Add(new StartupToast(toast, sticky, item));
            if (_startupToasts.Count >= 2)
            {
                _ShowStartupBundle(_startupToasts);
            }

            return toast;
        }

        _Show(toast, sticky);

        return toast;
    }

    internal ToastViewModel AddCondition(string key, string message, ToastSeverity severity,
        IReadOnlyList<ToastAction> actions, Func<bool> stillApplies)
    {
        var deadline = DateTimeOffset.UtcNow + AutoDismissDelay;
        var visible = true;
        if (_conditions.Remove(key, out var previous))
        {
            deadline = previous.Deadline;
            visible = Toasts.Contains(previous.Toast);
            _Resolve(previous);
        }

        var toast = Add(message, severity, actions, sticky: true, key: key);
        var item = Notifications.Items.First(item => item.Key == key && item.SourceId is null);
        var condition = new ConditionToast(toast, item, stillApplies, deadline);
        _conditions[key] = condition;
        if (_automaticallyRecheck && _conditionTimer is null)
        {
            _conditionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _conditionTimer.Tick += (_, _) => RecheckConditions();
            _conditionTimer.Start();
        }
        foreach (var action in toast.Actions)
        {
            action.StillApplies = stillApplies;
        }

        if (!visible || deadline <= DateTimeOffset.UtcNow)
        {
            toast.CloseCommand.Execute(null);
        }
        else
        {
            _scheduleAutoDismiss(toast, deadline - DateTimeOffset.UtcNow);
        }

        RecheckConditions();
        return toast;
    }

    internal void RecheckConditions()
    {
        foreach (var (key, condition) in _conditions.ToArray())
        {
            if (!condition.StillApplies())
            {
                _conditions.Remove(key);
                _Resolve(condition);
            }
        }

        if (_conditions.Count == 0)
        {
            _conditionTimer?.Stop();
            _conditionTimer = null;
        }
    }

    private static void _Resolve(ConditionToast condition)
    {
        foreach (var action in condition.Toast.Actions)
        {
            action.StillApplies = static () => false;
            action.InvokeCommand.NotifyCanExecuteChanged();
        }

        condition.Item.MarkResolved();
        condition.Toast.CloseCommand.Execute(null);
    }

    private sealed record ConditionToast(ToastViewModel Toast, NotificationItemViewModel Item,
        Func<bool> StillApplies, DateTimeOffset Deadline);

    // AC-1804: a plugin's notification, kept under its id while it needs you.
    internal ToastViewModel Notify(string sourceId, string source, PluginNotification notification) =>
        Add(notification.Message, DesktopBackendHost.ToToastSeverity(notification.Severity), [.. notification.Actions.Select(action => new ToastAction(action.Label, action.OnInvoke))],
            sticky: notification.Actions.Count > 0, source, notification.Key, sourceId, notification.Title);

    internal void AddNotificationSource(string sourceId, Func<string, Task<PluginNotification?>> recheck) => _sources[sourceId] = recheck;

    // AC-1804: after the plugins registered, each kept item is asked again: still standing comes back live, else resolved.
    internal async Task RestoreNotificationsAsync(string path)
    {
        foreach (var kept in NotificationCenterViewModel.ReadKept(path))
        {
            if (Notifications.Items.Any(item => item.SourceId == kept.SourceId && item.Key == kept.Key))
            {
                continue;
            }

            if (await _RecheckAsync(kept) is { } again)
            {
                Notify(kept.SourceId, kept.Source, again);
            }
            else
            {
                Notifications.Post(new ToastViewModel(kept.Message, kept.Severity, []) { Title = kept.Title }, kept.Source, kept.Key, kept.SourceId).MarkResolved();
            }

            // AC-1853: under "Still open from your last session", at the time it first came in.
            // A recheck may answer under another key; that one is new, not kept.
            Notifications.Items.FirstOrDefault(item => item.SourceId == kept.SourceId && item.Key == kept.Key)?.KeptFrom(kept.Time);
        }

        Notifications.KeepAt(path);
    }

    // A source that is gone or throws cannot vouch for its buttons, so the item reads as resolved.
    private async Task<PluginNotification?> _RecheckAsync(KeptNotification kept)
    {
        try
        {
            return _sources.TryGetValue(kept.SourceId, out var recheck) ? await recheck(kept.Key) : null;
        }
        catch (Exception)
        {
            return null;
        }
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

    // AC-1853 (mockup screen 02): the bundle names its first three items, then "+N more".
    private void _ShowStartupBundle(IReadOnlyList<StartupToast> startupToasts)
    {
        _startupBundleToast?.CloseCommand.Execute(null);
        var items = startupToasts.Select(startupToast => startupToast.Item).ToList();
        var needYou = items.Count(item => item.NeedsYou);
        // Captured: the bundle stays after the window ends, and its Show must still open the panel then.
        var show = _showStartupNotifications;
        var startupToast = new ToastViewModel(
            $"{items.Count} notifications at startup",
            ToastSeverity.Warning,
            [new ToastAction("Show notifications", () => show?.Invoke()), new ToastAction("Later", static () => { })])
        {
            Note = needYou == items.Count ? " · all need you" : needYou > 0 ? $" · {needYou} need you" : null,
            Lines = [.. items.Take(MaxBundleLines).Select(item => new ToastLineViewModel(item.Glyph, item.BrushKey, item.Source, item.Title ?? item.Message))],
            MoreLabel = items.Count > MaxBundleLines ? $"+{items.Count - MaxBundleLines} more" : null,
        };
        startupToast.Dismissed += (_, _) => Toasts.Remove(startupToast);
        Toasts.Add(startupToast);
        _startupBundleToast = startupToast;
    }

    private void _Show(ToastViewModel toast, bool sticky)
    {
        Toasts.Add(toast);

        if (!sticky && toast.Actions.Count == 0 && toast.Severity is ToastSeverity.Information or ToastSeverity.Success)
        {
            _scheduleAutoDismiss(toast, AutoDismissDelay);
        }
    }

    private sealed record StartupToast(ToastViewModel Toast, bool Sticky, NotificationItemViewModel Item);

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
