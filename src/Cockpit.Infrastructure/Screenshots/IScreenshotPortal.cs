using Tmds.DBus;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Tmds.DBus proxy contract for <c>org.freedesktop.portal.Screenshot</c>. The method name maps 1:1 onto the
/// D-Bus interface (Tmds.DBus generates the proxy from this shape at runtime), the same way
/// <c>IGlobalShortcutsPortal</c> does for push-to-talk.
/// </summary>
[DBusInterface("org.freedesktop.portal.Screenshot")]
public interface IScreenshotPortal : IDBusObject
{
    /// <summary>
    /// Asks the desktop for a screenshot. With <c>interactive: true</c> in the options this is the desktop's own
    /// picker — Spectacle on KDE, the shell's screenshot UI on GNOME — so region, window and full screen are the
    /// operator's choice there rather than three separate calls here. Returns a Request path; the image's
    /// <c>uri</c> arrives on that Request's Response signal.
    /// </summary>
    Task<ObjectPath> ScreenshotAsync(string parentWindow, IDictionary<string, object> options);
}
