using Avalonia;
using Avalonia.Controls;
using Cockpit.App.Controls;
using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-442's invariant: a pane shown as a miniature keeps the grid its pty already has. The two tests only
/// mean something together — the first pins the invariant, the second is the mutation kept runnable, so the
/// first is known to be red without the fix rather than green on a terminal that never resized anyway.
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
            var host = new MiniatureHost { Child = terminal, UseLayoutRounding = false };

            Layout(host, FullWidth, FullHeight);
            await SettleAsync(terminal);
            full = Grid(terminal);
            childBefore = host.Child;

            // Everything the pty could hear about happens from here on.
            terminal.Resized += (_, e) => resizes.Add(e);

            // Into the rail: the tile is the pane at MiniatureScale, which is what the rail hands it.
            host.Scale = MiniatureScale;
            Layout(host, FullWidth * MiniatureScale, FullHeight * MiniatureScale);
            await SettleAsync(terminal);
            mini = Grid(terminal);

            // And back to focus.
            host.Scale = 1.0;
            Layout(host, FullWidth, FullHeight);
            await SettleAsync(terminal);
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

    /// <summary>
    /// The mutation, kept runnable: the same terminal in the same tile without the host's scaling. It is the
    /// evidence that the invariant test above can fail — remove the transform from
    /// <see cref="MiniatureHost"/> and it degenerates into exactly this.
    /// </summary>
    [Fact]
    public async Task WithoutTheHost_TheSameTileReshapesThePty()
    {
        (int Cols, int Rows) full = default, tiled = default;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var terminal = NewTerminal();

            Layout(terminal, FullWidth, FullHeight);
            await SettleAsync(terminal);
            full = Grid(terminal);

            Layout(terminal, FullWidth * MiniatureScale, FullHeight * MiniatureScale);
            await SettleAsync(terminal);
            tiled = Grid(terminal);
        });

        Assert.True(tiled.Cols < full.Cols / 2,
            $"a {MiniatureScale:P0} tile left the grid at {tiled.Cols}x{tiled.Rows} against {full.Cols}x{full.Rows}; "
            + "if a real tile no longer collapses the grid, the invariant test above stopped proving anything");
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

    // The control's resize debounce is 50ms; 150ms leaves room for a dispatcher timer running late on a
    // loaded machine, then the poll waits out a grid still in motion.
    private static async Task SettleAsync(TerminalControl terminal)
    {
        var seen = terminal.Buffer.Rows;
        await Task.Delay(150);

        for (var poll = 0; poll < 12; poll++)
        {
            if (terminal.Buffer.Rows == seen)
            {
                return;
            }

            seen = terminal.Buffer.Rows;
            await Task.Delay(50);
        }
    }
}
