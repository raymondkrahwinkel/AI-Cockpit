using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.Notifications;

public sealed class PendingNotificationSounds(Func<Action, TimeSpan, IDisposable> schedule) : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);
    private readonly Dictionary<(string PaneId, SessionStatus Status), IDisposable> _pending = [];

    public void Schedule(string paneId, SessionStatus status, Func<bool> stillEligible, Func<Func<bool>, Task> play)
    {
        var key = (paneId, status);
        if (_pending.Remove(key, out var previous))
        {
            previous.Dispose();
        }

        IDisposable? timer = null;
        bool IsCurrent() => _pending.TryGetValue(key, out var current) && ReferenceEquals(current, timer);
        timer = schedule(async () =>
        {
            if (!IsCurrent())
            {
                return;
            }

            try
            {
                if (stillEligible())
                {
                    await play(() => IsCurrent() && stillEligible());
                }
            }
            finally
            {
                if (IsCurrent())
                {
                    _pending.Remove(key);
                }

                timer!.Dispose();
            }
        }, Delay);
        _pending[key] = timer;
    }

    public void RemoveSession(string paneId)
    {
        foreach (var key in _pending.Keys.Where(key => key.PaneId == paneId).ToArray())
        {
            _pending[key].Dispose();
            _pending.Remove(key);
        }
    }

    public void Dispose()
    {
        foreach (var timer in _pending.Values)
        {
            timer.Dispose();
        }

        _pending.Clear();
    }
}
