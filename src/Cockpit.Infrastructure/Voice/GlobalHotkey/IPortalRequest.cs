using Tmds.DBus;

namespace Cockpit.Infrastructure.Voice.GlobalHotkey;

/// <summary>
/// Tmds.DBus proxy contract for <c>org.freedesktop.portal.Request</c> — every XDG portal method call
/// (CreateSession, BindShortcuts, ...) only hands back a request object path; the actual result arrives
/// asynchronously as this object's single <c>Response</c> signal.
/// </summary>
[DBusInterface("org.freedesktop.portal.Request")]
public interface IPortalRequest : IDBusObject
{
    Task<IDisposable> WatchResponseAsync(Action<(uint ResponseCode, IDictionary<string, object> Results)> handler);
}
