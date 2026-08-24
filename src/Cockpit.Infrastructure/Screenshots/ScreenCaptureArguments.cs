using System.Globalization;

namespace Cockpit.Infrastructure.Screenshots;

// What `screencapture` is told to do (AC-328). Its own file because the arguments are the whole difference
// between AC-220's behaviour and this one, and an argument list is the one part of shelling out to a Mac binary
// that can be held to a test from anywhere.
internal static class ScreenCaptureArguments
{
    // Captures one display, silently (`-x`, no shutter sound) and by index (`-D`, so the right
    // display is targeted when several are attached). Deliberately omits `-i` (interactive picker) —
    // that selection UI is what this tool exists to own itself, not borrow from `screencapture`.
    public static IReadOnlyList<string> ForDisplay(int displayIndex, string path) =>
        ["-x", "-D", displayIndex.ToString(CultureInfo.InvariantCulture), path];
}
