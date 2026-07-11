using Cockpit.App.Views;
using Exclr8.Terminal.Buffer;
using FluentAssertions;

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
        TtyDiagnosticsSnapshot.Capture(null).Should().Be("buffer=?");
    }

    [Fact]
    public void Capture_FreshBuffer_ReportsGridCursorAndNoSelection()
    {
        var buffer = new TerminalBuffer(80, 24);

        var snapshot = TtyDiagnosticsSnapshot.Capture(buffer);

        snapshot.Should().Contain("cursor=(0,0)");
        snapshot.Should().Contain("region=(0..23)");
        snapshot.Should().Contain("scrollOffset=0");
        snapshot.Should().Contain("grid=80x24");
        snapshot.Should().Contain("altScreen=False");
        snapshot.Should().Contain("selection=none");
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
        snapshot.Should().Contain("anchor=(0,0)");
        snapshot.Should().Contain("active=(0,4)");
        snapshot.Should().Contain("mode=Word");
    }

    [Fact]
    public void Capture_AfterResize_ReportsNewGridAndClearedSelection()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.Write("hello"u8);
        buffer.SelectWord(row: 0, col: 2);

        buffer.Resize(120, 40);

        var snapshot = TtyDiagnosticsSnapshot.Capture(buffer);
        snapshot.Should().Contain("grid=120x40");
        snapshot.Should().Contain("region=(0..39)");
        // Resize clears any active selection (TerminalBuffer.Resize) — the #58 trigger is a double-click
        // (selection) followed by a resize-adjacent glitch, so this is the exact state transition to watch.
        snapshot.Should().Contain("selection=none");
    }
}
