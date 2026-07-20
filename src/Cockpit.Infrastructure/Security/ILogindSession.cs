using Tmds.DBus;

namespace Cockpit.Infrastructure.Security;

/// <summary>
/// Tmds.DBus proxy contract for a single <c>org.freedesktop.login1.Session</c>. The lock state AC-5 reacts to is the
/// <c>LockedHint</c> property, watched through <c>PropertiesChanged</c> rather than the session's <c>Lock</c>/
/// <c>Unlock</c> signals: GNOME only raises those signals for <c>loginctl lock-session</c>, not for an interactive
/// Super+L or an idle lock, whereas both GNOME (since 2016) and KDE (since Plasma 5.20) set <c>LockedHint</c> on every
/// lock and unlock. <see cref="GetAsync{T}"/> reads the current hint once at startup to catch a screen that was
/// already locked before the watch was in place.
/// </summary>
[DBusInterface("org.freedesktop.login1.Session")]
public interface ILogindSession : IDBusObject
{
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);

    Task<T> GetAsync<T>(string prop);
}
