using Cockpit.App.Views;
using Exclr8.Terminal.Buffer;

namespace Cockpit.Core.Tests.Views;

/// <summary>
/// The #58 TTY-glitch diagnostic snapshot formatter. <see cref="TerminalBuffer"/> is a pure, non-Avalonia
/// type (no UI thread needed to construct one), so these exercise the real Exclr8 buffer rather than a
/// fake — proving the public-property access documented on <see cref="TtyDiagnosticsSnapshot"/> actually
/// works against the shipped Exclr8.Terminal 1.0.7 surface.
/// </summary>
public class TtyDiagnosticsSnapshotTests
{
    [Fact]
    public void Capture_NullBuffer_ReturnsPlaceholder()
    {
        Assert.Equal("buffer=?", TtyDiagnosticsSnapshot.Capture(null));
    }

    [Fact]
    public void Capture_FreshBuffer_ReportsGridCursorAndNoSelection()
    {
        var buffer = new TerminalBuffer(80, 24);

        var snapshot = TtyDiagnosticsSnapshot.Capture(buffer);

        Assert.Contains("cursor=(0,0)", snapshot);
        Assert.Contains("region=(0..23)", snapshot);
        Assert.Contains("scrollOffset=0", snapshot);
        Assert.Contains("grid=80x24", snapshot);
        Assert.Contains("altScreen=False", snapshot);
        Assert.Contains("selection=none", snapshot);
    }

    [Fact]
    public void Capture_AfterSelectWord_ReportsAnchorAndActive()
    {
        // Mirrors TerminalControl.OnPointerPressed's double-click path (#58 repro trigger): write some
        // text so there's a word to select, then select it the same way Exclr8 does internally.
        var buffer = new TerminalBuffer(80, 24);
        buffer.Write("hello world"u8);

        buffer.SelectWord(row: 0, col: 2);

        var snapshot = TtyDiagnosticsSnapshot.Capture(buffer);
        Assert.Contains("anchor=(0,0)", snapshot);
        Assert.Contains("active=(0,4)", snapshot);
        Assert.Contains("mode=Word", snapshot);
    }

    [Fact]
    public void Capture_AfterResize_ReportsNewGridAndClearedSelection()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.Write("hello"u8);
        buffer.SelectWord(row: 0, col: 2);

        buffer.Resize(120, 40);

        var snapshot = TtyDiagnosticsSnapshot.Capture(buffer);
        Assert.Contains("grid=120x40", snapshot);
        Assert.Contains("region=(0..39)", snapshot);
        // Resize clears any active selection (TerminalBuffer.Resize) — the #58 trigger is a double-click
        // (selection) followed by a resize-adjacent glitch, so this is the exact state transition to watch.
        Assert.Contains("selection=none", snapshot);
    }
}
