using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// A small GDI surface a test draws itself, to stand in for the screen where the screen cannot answer: nothing
/// about a real desktop's pixels is known, so nothing about a capture of it proves which way up it came back.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DrawnSurface : IDisposable
{
    private const int Whiteness = 0x00FF0062;
    private const int Blackness = 0x00000042;

    private readonly IntPtr _screen;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _previous;
    private readonly int _width;

    public DrawnSurface(int width, int height)
    {
        _width = width;
        _screen = GetDC(IntPtr.Zero);

        // Compatible with the screen rather than with the memory context: a fresh memory context holds a 1×1
        // monochrome bitmap, and a colour bitmap made compatible with that one is monochrome too.
        DeviceContext = CreateCompatibleDC(_screen);
        _bitmap = CreateCompatibleBitmap(_screen, width, height);
        _previous = SelectObject(DeviceContext, _bitmap);
    }

    public IntPtr DeviceContext { get; }

    public void FillRows(int top, int height, bool white) =>
        PatBlt(DeviceContext, 0, top, _width, height, white ? Whiteness : Blackness);

    public void Dispose()
    {
        SelectObject(DeviceContext, _previous);
        DeleteObject(_bitmap);
        DeleteDC(DeviceContext);
        ReleaseDC(IntPtr.Zero, _screen);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PatBlt(IntPtr deviceContext, int x, int y, int width, int height, int operation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
