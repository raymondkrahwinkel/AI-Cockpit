using Tmds.DBus;

namespace Cockpit.Infrastructure.Security;

/// <summary>
/// Tmds.DBus proxy contract for a single <c>org.freedesktop.login1.Session</c>. AC-5 watches <c>LockedHint</c> via
/// <c>PropertiesChanged</c> rather than <c>Lock</c>/<c>Unlock</c> signals: GNOME only raises those for
/// <c>loginctl lock-session</c>, not Super+L/idle lock, whereas GNOME/KDE set <c>LockedHint</c> on every lock/unlock.
/// </summary>
[DBusInterface("org.freedesktop.login1.Session")]
public interface ILogindSession : IDBusObject
{
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);

    Task<T> GetAsync<T>(string prop);
}
