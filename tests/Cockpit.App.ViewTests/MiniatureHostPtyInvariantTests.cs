using Avalonia;
using Avalonia.Controls;
using Cockpit.App.Controls;
using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-442's invariant: a pane shown as a miniature keeps the grid its pty already has.
/// </summary>
[Collection("avalonia")]
public class MiniatureHostPtyInvariantTests
{
    // The focus pane at its natural size, and the miniature scale the mockup's rail draws it at.
    private const double FullWidth = 1000;
    private const double FullHeight = 640;
    private const double MiniatureScale = 0.28;

    [Fact]
    public async Task APaneGoingInAndOutOfMiniature_NeverChangesTheGridThePtyGets()
    {
        var resizes = new List<(int Cols, int Rows)>();
        (int Cols, int Rows) full = default, mini = default, back = default;
        Control? childBefore = null, childAfter = null;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var terminal = NewTerminal();
            var host = new MiniatureHost
            {
                Child = terminal,
                UseLayoutRounding = false,
                FocusSize = new Size(FullWidth, FullHeight),
                TileSize = new Size(FullWidth, FullHeight),
            };

            Layout(host, FullWidth, FullHeight);
            await TerminalSettle.WaitAsync(terminal);
            full = Grid(terminal);
            childBefore = host.Child;

            // Everything the pty could hear about happens from here on.
            terminal.Resized += (_, e) => resizes.Add(e);

            // Into the rail: the tile is the pane at MiniatureScale, which is what the rail hands it.
            host.TileSize = new Size(FullWidth * MiniatureScale, FullHeight * MiniatureScale);
            Layout(host, FullWidth * MiniatureScale, FullHeight * MiniatureScale);
            await TerminalSettle.WaitAsync(terminal);
            mini = Grid(terminal);

            // And back to focus.
            host.TileSize = new Size(FullWidth, FullHeight);
            Layout(host, FullWidth, FullHeight);
            await TerminalSettle.WaitAsync(terminal);
            back = Grid(terminal);
            childAfter = host.Child;
        });

        Assert.True(full.Cols > 0 && full.Rows > 0, "the harness measured no grid, so it proves nothing");
        Assert.Equal(full, mini);
        Assert.Equal(full, back);
        Assert.Empty(resizes);

        // AC-442 #4: the same control throughout — the switch is a number, not a rebuilt pane.
        Assert.Same(childBefore, childAfter);
    }

    private static TerminalControl NewTerminal() =>
        // Layout rounding off so the host's divide-by-scale reaches the child exactly, the same reason
        // TerminalGridFitsBoundsTests turns it off.
        new() { FontSize = 14, UseLayoutRounding = false };

    private static (int Cols, int Rows) Grid(TerminalControl terminal) =>
        (terminal.Buffer.Cols, terminal.Buffer.Rows);

    private static void Layout(Control control, double width, double height)
    {
        var size = new Size(width, height);
        control.Measure(size);
        control.Arrange(new Rect(size));
    }
}
