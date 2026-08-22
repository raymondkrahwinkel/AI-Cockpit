using Tmds.DBus;

namespace Cockpit.Infrastructure.Security;

/// <summary>
/// Tmds.DBus proxy contract for systemd-logind's <c>org.freedesktop.login1.Manager</c> on the system bus (AC-5).
/// Method names map 1:1 onto the D-Bus members (Tmds.DBus generates the proxy at runtime), so
/// <see cref="GetSessionAsync"/> must stay named for the <c>GetSession</c> member.
/// </summary>
[DBusInterface("org.freedesktop.login1.Manager")]
public interface ILogindManager : IDBusObject
{
    /// <summary>
    /// Turns a logind session id into its object path — the operator's own <c>XDG_SESSION_ID</c>, or the literal
    /// <c>"auto"</c>, which logind resolves server-side to the caller's or display session. Covers a cockpit
    /// launched from an AppImage/<c>.desktop</c> entry (<c>app.slice</c>), where <c>GetSessionByPID</c> failed with <c>NoSessionForPID</c>.
    /// </summary>
    Task<ObjectPath> GetSessionAsync(string sessionId);
}
