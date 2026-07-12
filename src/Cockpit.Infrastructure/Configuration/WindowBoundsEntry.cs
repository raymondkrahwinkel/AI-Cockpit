using Cockpit.Core.Layout;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of the main window's bounds, under the <c>windowBounds</c> section of <c>cockpit.json</c>.</summary>
internal sealed class WindowBoundsEntry
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsMaximized { get; set; }

    public static WindowBoundsEntry FromDomain(WindowBounds bounds) => new()
    {
        X = bounds.X,
        Y = bounds.Y,
        Width = bounds.Width,
        Height = bounds.Height,
        IsMaximized = bounds.IsMaximized,
    };

    public WindowBounds ToDomain() => new(X, Y, Width, Height, IsMaximized);
}
