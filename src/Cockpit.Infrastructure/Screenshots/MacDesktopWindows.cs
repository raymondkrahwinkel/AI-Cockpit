using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// The windows on a macOS desktop (AC-330), through `CGWindowListCopyWindowInfo` — which reports bounds and
// layer for every on-screen window, front to back, and needs no Screen Recording permission to do it: geometry
// is not pixels.
// Bounds are in points, the same space `CGDisplayBounds` speaks and the same the capture's
// `CapturedDisplay.DesktopBounds` carries on this platform, so nothing is converted here.
//
// Unverified: there is no Mac. Kept thin for that reason, with the picking itself in
// `ScreenshotSelectionViewModel` where it is tested. The window list is read through Core Foundation's own
// accessors rather than by laying a struct over the dictionaries, because their layout is not public.
[SupportedOSPlatform("macos")]
internal sealed class MacDesktopWindows : IDesktopWindows
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // `kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements` — what the operator can actually see and point at.
    private const uint OnScreenWindows = 1 | 16;

    private const uint NullWindow = 0;

    // The layer ordinary application windows live on. Menu bars, docks and shielding windows sit above it and are not things to screenshot.
    private const int NormalLayer = 0;

    private const int Float64 = 6;

    private const uint Utf8 = 0x08000100;

    public bool IsSupported => true;

    public IReadOnlyList<DesktopWindow> Enumerate()
    {
        var list = CGWindowListCopyWindowInfo(OnScreenWindows, NullWindow);
        if (list == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            // Already front to back: CGWindowListCopyWindowInfo documents the on-screen list as ordered front to
            // back, which is the order a picker wants.
            return Enumerable.Range(0, (int)CFArrayGetCount(list))
                .Select(index => _Describe(CFArrayGetValueAtIndex(list, index)))
                .OfType<DesktopWindow>()
                .ToList();
        }
        finally
        {
            CFRelease(list);
        }
    }

    private static DesktopWindow? _Describe(IntPtr window)
    {
        if (_NumberOf(window, "kCGWindowLayer") is not { } layer || (int)layer != NormalLayer)
        {
            return null;
        }

        var bounds = _ValueOf(window, "kCGWindowBounds");
        if (bounds == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(bounds, out var rectangle))
        {
            return null;
        }

        var left = (int)Math.Floor(rectangle.Origin.X);
        var top = (int)Math.Floor(rectangle.Origin.Y);
        var frame = new CaptureRect(
            left,
            top,
            (int)Math.Ceiling(rectangle.Origin.X + rectangle.Size.Width) - left,
            (int)Math.Ceiling(rectangle.Origin.Y + rectangle.Size.Height) - top);

        return frame is { Width: > 0, Height: > 0 }
            ? new DesktopWindow { Title = _StringOf(window, "kCGWindowName") ?? _StringOf(window, "kCGWindowOwnerName") ?? string.Empty, Bounds = frame }
            : null;
    }

    private static double? _NumberOf(IntPtr dictionary, string key)
    {
        var value = _ValueOf(dictionary, key);
        return value != IntPtr.Zero && CFNumberGetValue(value, Float64, out double number) ? number : null;
    }

    // A window's name is optional — an application that has not granted the cockpit accessibility rights
    // reports none — so the owner's name stands in, which is the more useful label anyway.
    private static string? _StringOf(IntPtr dictionary, string key)
    {
        var value = _ValueOf(dictionary, key);
        if (value == IntPtr.Zero)
        {
            return null;
        }

        var length = (int)CFStringGetLength(value);
        if (length == 0)
        {
            return null;
        }

        var buffer = new byte[(length * 4) + 1];
        return CFStringGetCString(value, buffer, buffer.Length, Utf8)
            ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0')
            : null;
    }

    // One entry out of a window's dictionary. The key has to be a Core Foundation string, which is created here
    // and released again — Create owns what it returns, and a key made per lookup and left behind leaks once per
    // field per window per capture. What comes back is the dictionary's own and is not released.
    private static IntPtr _ValueOf(IntPtr dictionary, string key)
    {
        var name = CFStringCreateWithCString(IntPtr.Zero, key, Utf8);
        try
        {
            return CFDictionaryGetValue(dictionary, name);
        }
        finally
        {
            CFRelease(name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeTo);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dictionary, out CGRect rectangle);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CFNumberGetValue(IntPtr number, int type, out double value);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(IntPtr text);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CFStringGetCString(IntPtr text, byte[] buffer, nint size, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr reference);
}
