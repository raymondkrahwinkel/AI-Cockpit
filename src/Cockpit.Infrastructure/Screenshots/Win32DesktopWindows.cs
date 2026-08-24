using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// AC-1013 (AC-330): windows via `EnumWindows` (front-most-first order gives stacking for free). Bounds use
// `DWMWA_EXTENDED_FRAME_BOUNDS`, not `GetWindowRect`, to exclude the invisible resize border that would
// otherwise crop in someone else's screen. Cloaked windows (suspended UWP, other virtual desktop) are skipped.
[SupportedOSPlatform("windows")]
internal sealed class Win32DesktopWindows : IDesktopWindows
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    // Long enough for any title worth showing; a window that has more says as much of it as fits.
    private const int MaxTitle = 256;

    public bool IsSupported => true;

    public IReadOnlyList<DesktopWindow> Enumerate()
    {
        var windows = new List<DesktopWindow>();
        EnumWindows((handle, _) =>
        {
            if (_Describe(handle) is { } window)
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static DesktopWindow? _Describe(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || _IsCloaked(handle))
        {
            return null;
        }

        var title = new StringBuilder(MaxTitle);
        if (GetWindowText(handle, title, MaxTitle) == 0)
        {
            // No title. Toolbars, the desktop's own layers and a great many invisible helper windows have none,
            // and a picker that offered them would be a list of rectangles nobody recognises.
            return null;
        }

        if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out Rect bounds, Marshal.SizeOf<Rect>()) != 0)
        {
            return null;
        }

        var rectangle = new CaptureRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        return rectangle is { Width: > 0, Height: > 0 }
            ? new DesktopWindow { Title = title.ToString(), Bounds = rectangle }
            : null;
    }

    private static bool _IsCloaked(IntPtr handle) =>
        DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out Rect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
