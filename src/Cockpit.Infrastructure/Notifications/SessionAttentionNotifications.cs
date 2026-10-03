using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Notifications;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Notifications;

// AC-1467: a backend without a frontend says what the desktop's CockpitViewModel says — a session needing a decision,
// and a session done — on the same edges and with the same words. Registered only without a frontend, so a desktop
// never says it twice. SDK only: a TTY pane keeps no host-owned fold to raise LiveStateChanged (AC-294).
internal sealed class SessionAttentionNotifications(
    ISessionRegistry registry,
    IAttentionNotifier notifier,
    ILogger<SessionAttentionNotifications> logger) : IHostedService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ISessionHandle, Action> _watched = new(ReferenceEqualityComparer.Instance);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Changed += _OnRegistryChanged;
        _Follow();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        registry.Changed -= _OnRegistryChanged;
        lock (_gate)
        {
            foreach (var unsubscribe in _watched.Values)
            {
                unsubscribe();
            }

            _watched.Clear();
        }

        return Task.CompletedTask;
    }

    private void _OnRegistryChanged(object? sender, EventArgs e) => _Follow();

    // `All` leaves the assistant out, as the desktop does (AC-735).
    private void _Follow()
    {
        lock (_gate)
        {
            var live = registry.All.ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var gone in _watched.Keys.Where(handle => !live.Contains(handle)).ToList())
            {
                _watched[gone]();
                _watched.Remove(gone);
            }

            foreach (var handle in live.Where(handle => !_watched.ContainsKey(handle)))
            {
                var watch = new Watch(handle.LiveState);
                Action<SessionLiveState> onLiveState = _ => _Decide(handle, watch, turnEnded: false);
                Action<SessionTurnEnd> onTurnEnded = _ => _Decide(handle, watch, turnEnded: true);
                handle.LiveStateChanged += onLiveState;
                handle.TurnEnded += onTurnEnded;
                _watched[handle] = () =>
                {
                    handle.LiveStateChanged -= onLiveState;
                    handle.TurnEnded -= onTurnEnded;
                };
            }
        }
    }

    // CockpitViewModel's edges, read from the handle's current state: two threads may raise its signals out of order.
    // A turn that ended arms "Done" once, so a raise that comes late neither loses nor repeats it; a shell holds it
    // back until it ends (AC-276).
    private void _Decide(ISessionHandle handle, Watch watch, bool turnEnded)
    {
        bool attention;
        bool finished;
        lock (watch.Gate)
        {
            var current = handle.LiveState;
            watch.TurnEnded |= turnEnded;
            attention = current.Status == SessionStatus.NeedsAttention && watch.Last.Status != SessionStatus.NeedsAttention;
            finished = current.Status == SessionStatus.Done && !_HasShells(current)
                && (watch.TurnEnded || (watch.Last.Status == SessionStatus.Done && _HasShells(watch.Last)));
            if (finished || current.Status is SessionStatus.Failed or SessionStatus.NeedsAttention)
            {
                watch.TurnEnded = false;
            }

            watch.Last = current;
        }

        if (attention)
        {
            _Deliver(handle, () => notifier.NotifyAttentionAsync(new AttentionNotification(handle.Title, "Needs attention")));
        }

        if (finished)
        {
            _Deliver(handle, () => notifier.NotifySessionFinishedAsync(new AttentionNotification(handle.Title, "Done"), isSelected: false, isWindowActive: false));
        }
    }

    private static bool _HasShells(SessionLiveState state) => state.BackgroundTasks.Any(task => task.Kind == BackgroundTaskKind.Shell);

    // Not awaited: the event is raised on the thread that folded it. A failure is logged, not lost.
    private void _Deliver(ISessionHandle handle, Func<Task> send) => _ = _DeliverAsync(handle.PaneId, send);

    private async Task _DeliverAsync(string paneId, Func<Task> send)
    {
        try
        {
            await send().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Session {Pane}: its attention notification could not be delivered.", paneId);
        }
    }

    // One pane's last read state and whether a turn ended since its last "Done", under its own gate.
    private sealed class Watch(SessionLiveState last)
    {
        public Lock Gate { get; } = new();

        public SessionLiveState Last { get; set; } = last;

        public bool TurnEnded { get; set; }
    }
}
