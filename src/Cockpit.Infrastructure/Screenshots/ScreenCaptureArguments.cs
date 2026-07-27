using System.Globalization;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// What <c>screencapture</c> is told to do (AC-328). Its own file because the arguments are the whole difference
/// between AC-220's behaviour and this one, and an argument list is the one part of shelling out to a Mac binary
/// that can be held to a test from anywhere.
/// </summary>
internal static class ScreenCaptureArguments
{
    /// <summary>Captures one display, whole, silently, to the given path.</summary>
    /// <remarks>
    /// <c>-x</c> silences the shutter, a camera noise nobody asked for when the point is to hand an image to an
    /// agent. <c>-D</c> names the display; without it and with several attached, what the binary writes and
    /// where is not something the ticket's research could establish, so it is never left out.
    /// <para>
    /// What is <em>not</em> here is <c>-i</c>. That flag is the interactive selection — the crosshair, the
    /// spacebar-for-a-window — and it is exactly the UI this tool exists to own rather than borrow.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ForDisplay(int displayIndex, string path) =>
        ["-x", "-D", displayIndex.ToString(CultureInfo.InvariantCulture), path];
}
