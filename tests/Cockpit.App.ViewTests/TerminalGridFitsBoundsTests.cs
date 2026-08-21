using Avalonia;
using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The grid the terminal reports is the grid the pty gets, and the control clips to its bounds — so a row
/// the grid claims but the box cannot hold is drawn and never seen, with nothing to tell the operator it is
/// missing (AC-421). Two things have to hold at once, and each of these tests would pass on a change that
/// breaks the other:
/// <list type="bullet">
/// <item>every reported row fits — <see cref="ReportedRows_AlwaysFitInsideTheBounds_WhileTheHeightSweepsAcrossCellBoundaries"/>;</item>
/// <item>layout jitter around a cell boundary still does not reach the pty — <see cref="AWobbleAroundACellBoundary_SettlesOnceAndThenStopsResizing"/>,
/// which is what the boundary deadband exists for and what nothing covered before.</item>
/// </list>
/// <para>
/// A height sweep rather than a per-layout test on purpose: a layout can only ever hand the control some
/// height, so covering the heights covers the grid, stacked and zoomed layouts at once — and covers
/// fractional render scaling with them, which reaches the control as nothing but a fractional height.
/// </para>
/// </summary>
[Collection("avalonia")]
public class TerminalGridFitsBoundsTests
{
    /// <summary>Wide enough that the column count never flips, so the sweep varies one axis.</summary>
    private const double Width = 900;

    /// <summary>
    /// Row counts whose boundaries the sweep crosses. Well clear of <c>MinUsableRows</c>, where the control
    /// deliberately stops tracking the box at all — a separate behaviour, and not this test's subject.
    /// </summary>
    private static readonly int[] Boundaries = [18, 20, 21];

    [Fact]
    public async Task ReportedRows_AlwaysFitInsideTheBounds_WhileTheHeightSweepsAcrossCellBoundaries()
    {
        var violations = new List<string>();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var terminal = NewTerminal();
            SetHeight(terminal, 600);
            await TerminalSettle.WaitAsync(terminal);

            var cell = terminal.CellHeight;
            Assert.True(cell > 0, "the harness measured no cell height, so it would prove nothing about fit");

            foreach (var rows in Boundaries)
            {
                var boundary = rows * cell;
                foreach (var height in TargetHeights(boundary, cell))
                {
                    // Return to a settled grid of exactly `rows` before each target, so every sample is one
                    // known transition rather than a step in a walk. Walking is what hid the case this
                    // catches: land deep inside the cell below in a single move — a splitter yanked, a
                    // banner appearing — and a damped shrink strands a whole row, not a sliver of one.
                    //
                    // Three quarters of a cell above the boundary, because growth is damped: any smaller
                    // margin can be swallowed by the deadband, leaving the grid a row below what this loop
                    // then assumes it is starting from. That silently skipped the transition it exists for.
                    SetHeight(terminal, boundary + cell * 0.75);
                    await TerminalSettle.WaitAsync(terminal);

                    // If the reference did not take, the deadband refused the growth this loop assumes.
                    // Say so here: otherwise it surfaces further down as an invariant violation, which
                    // reads as the sample being wrong when the starting grid was.
                    Assert.True(terminal.Buffer.Rows == rows,
                        $"expected {rows} rows at {boundary + cell * 0.75:F2}px before the sample, got "
                        + $"{terminal.Buffer.Rows} — the grid never reached the reference");

                    SetHeight(terminal, height);
                    await TerminalSettle.WaitAsync(terminal);

                    var reported = terminal.Buffer.Rows;
                    var needed = reported * terminal.CellHeight;
                    if (needed > terminal.Bounds.Height + 0.01)
                    {
                        violations.Add(
                            $"height {terminal.Bounds.Height:F2} (from {rows} rows): reports {reported} rows, "
                            + $"which needs {needed:F2}px — {needed - terminal.Bounds.Height:F2}px past the bottom edge");
                    }
                }
            }
        });

        Assert.True(violations.Count == 0,
            $"{violations.Count} height(s) report a row that cannot be drawn inside the pane:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// A pane whose height twitches across a cell boundary — the focus-ring border flip the deadband was
    /// written for — must not keep re-sizing the pty. Each resize reframes the console screen buffer, which
    /// on Windows costs the blank rows a TUI drew. One settle onto the side that fits is the whole budget.
    /// </summary>
    [Fact]
    public async Task AWobbleAroundACellBoundary_SettlesOnceAndThenStopsResizing()
    {
        var resizes = 0;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var terminal = NewTerminal();
            SetHeight(terminal, 600);
            await TerminalSettle.WaitAsync(terminal);

            var cell = terminal.CellHeight;
            var boundary = 21 * cell;

            // 6px of travel. The deadband's own reasoning only claims a 2px-per-side chrome flip — 4px —
            // but the two-sided band this replaced absorbed 5px, and a fix may not quietly become a
            // regression on the very property it exists to keep. Measured: the band tolerates travel up to
            // its own width, so anything under 6px here would pass a band narrower than the one shipped.
            var low = boundary - 1;
            var high = boundary + 5;

            SetHeight(terminal, high);
            await TerminalSettle.WaitAsync(terminal);

            terminal.Resized += (_, _) => resizes++;

            for (var cycle = 0; cycle < 4; cycle++)
            {
                SetHeight(terminal, low);
                await TerminalSettle.WaitAsync(terminal);
                SetHeight(terminal, high);
                await TerminalSettle.WaitAsync(terminal);
            }
        });

        Assert.True(resizes <= 1,
            $"a 4px wobble across one cell boundary resized the pty {resizes} times over four cycles; "
            + "at most one settle onto the side that fits is allowed");
    }

    /// <summary>
    /// Zoomed all the way out — the control clamps the terminal font at 6pt — a cell is smaller than the
    /// deadband guarding it. Unless the band gives way to the cell, growth stops being reachable at all and
    /// a pane keeps a grid it has long outgrown: the same unusable-space failure approached from the other
    /// side, and one an operator reaches with a few presses of zoom-out.
    /// </summary>
    [Fact]
    public async Task AtTheSmallestFont_TheGridStillGrowsIntoSpaceThatOpensUp()
    {
        int before = 0, after = 0;
        double cell = 0;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var terminal = NewTerminal();
            terminal.FontSize = 6;

            SetHeight(terminal, 600);
            await TerminalSettle.WaitAsync(terminal);
            cell = terminal.CellHeight;

            SetHeight(terminal, 40 * cell + cell * 0.75);
            await TerminalSettle.WaitAsync(terminal);
            before = terminal.Buffer.Rows;

            SetHeight(terminal, 41 * cell + cell * 0.6);
            await TerminalSettle.WaitAsync(terminal);
            after = terminal.Buffer.Rows;
        });

        // Below twice the deadband, or the cap under test never engages and this proves nothing.
        Assert.True(cell < 12, $"the smallest font measured a {cell:F2}px cell, too tall to exercise the cap");
        Assert.True(after > before,
            $"with a {cell:F2}px cell the grid stayed at {before} rows after the pane grew past a boundary, "
            + "so zooming out leaves a strip the terminal will not use");
    }

    /// <summary>
    /// Heights to land on, spanning from just above one boundary down to just above the one below it.
    /// Close-in offsets because a boundary is where a grid flips — a fix validated only at round heights
    /// moves the failure a pixel or two and looks green. The tail is measured from the cell rather than in
    /// fixed pixels so the far end stays just inside the lower boundary whatever the font measures.
    /// </summary>
    private static IEnumerable<double> TargetHeights(double boundary, double cell) =>
        [
            boundary + 8, boundary + 2, boundary + 0.5,
            boundary - 0.5, boundary - 2.5,
            boundary - cell / 2,
            boundary - cell + 4, boundary - cell + 0.25,
        ];

    private static TerminalControl NewTerminal() =>
        // Layout rounding off, so the fractional heights above survive to the control. At scale 1 it would
        // snap every one of them to a whole pixel and the sweep would only ever test round heights; a pane
        // on a 1.25 or 1.5 display is rounded to *physical* pixels and arrives as a fraction of a DIP.
        // Turning it off is how that case is reached without a scaled window.
        new() { FontSize = 14, UseLayoutRounding = false };

    private static void SetHeight(TerminalControl terminal, double height)
    {
        var size = new Size(Width, height);
        terminal.Measure(size);
        terminal.Arrange(new Rect(size));
    }
}
