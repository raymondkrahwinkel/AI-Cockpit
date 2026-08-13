using Avalonia.Controls;

namespace Cockpit.App.Services;

// AC-765: every "surface this already-open window" click path (the assistant chat pop-out, the tray's "Show
// Cockpit") shared the same gap — Show() on an already-visible window is a no-op, so a minimized window stayed
// minimized. One helper, so the fix is not duplicated per caller.
internal static class WindowActivation
{
    // Activate() only asks the platform for focus — on X11/XWayland the window manager (KWin's focus-stealing
    // prevention, e.g.) may refuse it, and nothing here can force that.
    public static void BringToFront(Window window)
    {
        window.Show();

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
