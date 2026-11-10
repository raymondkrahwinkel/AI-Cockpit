using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Infrastructure.Plugins;

internal sealed class OperatorPhoneNotifications : IOperatorPhoneNotifications, ISingletonService
{
    private readonly Lock _gate = new();
    private Func<OperatorPhoneNotification, int>? _send;
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _senders = new(StringComparer.Ordinal);

    public IDisposable Register(Func<OperatorPhoneNotification, int> send)
    {
        lock (_gate)
        {
            if (_send is not null)
            {
                throw new InvalidOperationException("An operator phone transport is already registered.");
            }
            _send = send;
        }
        return new _Registration(this, send);
    }

    internal (int Accepted, string? Error) Send(OperatorPhoneNotification notification, DateTimeOffset now)
    {
        Func<OperatorPhoneNotification, int> send;
        lock (_gate)
        {
            if (_send is not { } registered)
            {
                return (0, "No operator phone transport is available.");
            }
            foreach (var sender in _senders.Where(entry => now - entry.Value.Start >= TimeSpan.FromMinutes(1)).Select(entry => entry.Key).ToArray())
            {
                _senders.Remove(sender);
            }
            var window = _senders.GetValueOrDefault(notification.SenderId, (Start: now, Count: 0));
            if (window.Count >= 3)
            {
                return (0, "Rate limit: at most three pings per sender per minute.");
            }
            _senders[notification.SenderId] = (window.Start, window.Count + 1);
            send = registered;
        }
        int accepted = send(notification);
        return (accepted, accepted == 0 ? "No connected phone accepted the ping." : null);
    }

    private sealed class _Registration(OperatorPhoneNotifications owner, Func<OperatorPhoneNotification, int> send) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (owner._send == send)
                {
                    owner._send = null;
                }
            }
        }
    }
}
