using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The controls that paint inside a <see cref="Avalonia.Controls.Control.Render"/> override. Nothing they draw
/// lands on a property, so the palette baseline AC-338 records walks straight past them: it reads brushes off
/// <c>Border</c>, <c>TemplatedControl</c>, <c>TextBlock</c>, <c>Panel</c> and <c>Shape</c>, and a colour handed
/// to a <c>DrawingContext</c> is on none of those. A bar could go back to a framework grey, orange or red and no
/// baseline anywhere would differ (AC-413).
/// <para>
/// So these read the colour back out of the frame instead, and compare it against the live token rather than a
/// hex — a repaint moves both together, a hardcoded colour moves only one.
/// </para>
/// </summary>
[Collection("avalonia")]
public class RenderedInkTests
{
    /// <summary>Tall enough for the 4px track to sit clear of the label's ascenders, short enough to stay a strip.</summary>
    private const double BarHeight = 20;

    /// <summary>Where a <see cref="LimitBar"/>'s track starts with an empty label: nothing, then the 5px gap.</summary>
    private const double TrackLeft = 5;

    [Fact]
    public void AFullLimitBar_PaintsTheThemesErrorColour_NotAFrameworkRed() => HeadlessAvalonia.Run(() =>
    {
        // The one that stings: this bar is the operator's warning that a limit is nearly gone, on a control no
        // baseline can see. What this pins is the ink, so replacing the lookup with a framework red turns it red.
        // It says nothing about the hex the lookup falls back to when there are no resources at all — that branch
        // is unreachable with a theme loaded, and is held to its token by ThemeHexColorGuardTests instead.
        var bar = _Bar(percent: 95);
        using var scene = RenderedScene.Show(bar);

        _AssertInk("CockpitStatusErrorBrush", bar, scene, TrackLeft + 5);
    });

    [Fact]
    public void ALimitBarPastItsThreshold_PaintsTheThemesWaitingColour_NotAFrameworkOrange() => HeadlessAvalonia.Run(() =>
    {
        // Past the threshold its provider declared, but not yet halfway from there to full.
        var bar = _Bar(percent: 85);
        using var scene = RenderedScene.Show(bar);

        _AssertInk("CockpitStatusWaitingBrush", bar, scene, TrackLeft + 5);
    });

    [Fact]
    public void AQuietLimitBar_PaintsItsFillAndItsTrackInTwoDifferentThemeColours() => HeadlessAvalonia.Run(() =>
    {
        // Both halves in one test on purpose: a fill and a track that resolved to the same token would satisfy either
        // assertion alone while leaving the bar a flat strip with nothing readable on it.
        var bar = _Bar(percent: 20);
        using var scene = RenderedScene.Show(bar);

        _AssertInk("CockpitTextSecondaryBrush", bar, scene, TrackLeft + 2);
        _AssertInk("CockpitHairlineBrush", bar, scene, TrackLeft + 20);
    });

    [Fact]
    public void LimitBarAndUsagePillsHaveTheSameSeverityForEveryWholePercentAndThreshold() => HeadlessAvalonia.Run(() =>
    {
        var fillFor = typeof(LimitBar).GetMethod("FillFor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        foreach (var threshold in Enumerable.Range(0, 101))
        {
            var bar = new LimitBar { Threshold = threshold };
            foreach (var percent in Enumerable.Range(0, 101))
            {
                var actual = Assert.IsType<SolidColorBrush>(fillFor.Invoke(bar, [percent]));
                Assert.Equal(_FallbackColourFor(UsageSeverity.BrushKeyFor(percent, threshold)), actual.Color);
            }
        }
    });

    [Fact]
    public void AMicMeterOverItsThreshold_PaintsTheThemesAccent() => HeadlessAvalonia.Run(() =>
    {
        // The meter says "this is loud enough to interrupt with" by colour alone, so which colour is the message.
        var meter = _Meter(level: 1, threshold: 0.5);
        using var scene = RenderedScene.Show(meter);

        _AssertInk("CockpitAccentBrush", meter, scene, 40);
    });

    [Fact]
    public void AMicMeterUnderItsThreshold_PaintsTheThemesDoneColour() => HeadlessAvalonia.Run(() =>
    {
        var meter = _Meter(level: 0.6, threshold: 0.9);
        using var scene = RenderedScene.Show(meter);

        _AssertInk("CockpitStatusDoneBrush", meter, scene, 40);
    });

    [Fact]
    public void AMicMeterAtRest_LeavesItsTrackInTheThemesHairline() => HeadlessAvalonia.Run(() =>
    {
        // Sampled left of the marker line, which is drawn on top at the threshold and is a colour of its own.
        var meter = _Meter(level: 0, threshold: 0.9);
        using var scene = RenderedScene.Show(meter);

        _AssertInk("CockpitHairlineBrush", meter, scene, 40);
    });

    [Fact]
    public void AMicMetersMarker_StandsOnTheThemesBrightestText() => HeadlessAvalonia.Run(() =>
    {
        // The colour this control resolves that the other three tests sample deliberately clear of. Without this,
        // the line marking where barge-in trips could go back to a plain white with everything else still green.
        //
        // An odd width so the marker lands on a half-pixel: it is drawn at threshold * width with a 1.5px pen, and
        // a 1.5px pen centred on a whole pixel covers two columns three-quarters each — no column outright, so
        // every sample would be an antialiased blend and the assertion could only be a tolerance.
        var meter = _Meter(level: 0, threshold: 0.5, width: 201);
        using var scene = RenderedScene.Show(meter);

        _AssertInk("CockpitTextPrimaryBrush", meter, scene, 100);
    });

    [Fact]
    public void TheDashboardGrid_DrawsItsCellsInTheThemesHairline() => HeadlessAvalonia.Run(() =>
    {
        // Through the real view, not a control this test hands a brush to. DashboardGridLines has no fallback of its
        // own — a null LineBrush makes Render return without drawing — so what is worth pinning is not "it paints
        // what it is given" but that CockpitView.axaml still gives it the token. A test that supplied the brush
        // itself would go on passing with that binding deleted, and the grid would simply be gone.
        using var scene = _Dashboard(columns: 4, rows: 4);

        var grid = scene.Window.GetVisualDescendants().OfType<DashboardGridLines>().Single();
        Assert.True(grid.IsEffectivelyVisible, "the grid has to be on screen before anything can be read off it");

        var hairline = RenderedScene.AsRendered(RenderedScene.TokenBrush("CockpitHairlineBrush"));

        // Divisions are drawn at Math.Round(size / count) + 0.5 with a 1px pen, which covers exactly the one pixel
        // row or column starting there — the half-pixel is what keeps them crisp. Both loops are sampled: they share
        // a pen today, and nothing else would notice if one of them stopped.
        Assert.Equal(hairline, _PaintedIn(grid, scene, Math.Round(grid.Bounds.Width / 4), grid.Bounds.Height / 8));
        Assert.Equal(hairline, _PaintedIn(grid, scene, grid.Bounds.Width / 8, Math.Round(grid.Bounds.Height / 4)));
    });

    /// <summary>The cockpit showing a dashboard workspace with its cell grid turned on.</summary>
    private static RenderedScene.Scene _Dashboard(int columns, int rows)
    {
        var dashboard = Workspace.Create("Dashboard", WorkspaceType.Dashboard)
            with { Layout = DashboardLayout.Default with { Columns = columns, Rows = rows, ShowGridLines = true } };

        var cockpit = new CockpitViewModel
        {
            Workspaces =
            {
                Settings = new WorkspaceSettings { Workspaces = [dashboard], ActiveWorkspaceId = dashboard.Id }.Normalized(),
            },
        };

        var window = new MainWindow { DataContext = cockpit, Width = 1100, Height = 760 };
        window.Show();
        window.UpdateLayout();

        return new RenderedScene.Scene(window);
    }

    /// <summary>A bar with no label, so the track starts at a known offset instead of behind glyphs of unknown width.</summary>
    private static LimitBar _Bar(double percent) => new()
    {
        Label = string.Empty,
        Percent = percent,
        Threshold = 80,
        Height = BarHeight,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static Color _FallbackColourFor(string brushKey) => brushKey switch
    {
        "CockpitStatusErrorBrush" => Color.Parse("#D64545"),
        "CockpitStatusWaitingBrush" => Color.Parse("#E0A33E"),
        "CockpitTextSecondaryBrush" => Color.Parse("#949AA5"),
        _ => throw new ArgumentOutOfRangeException(nameof(brushKey)),
    };

    private static MicLevelMeter _Meter(double level, double threshold, double width = 200) => new()
    {
        Level = level,
        Threshold = threshold,
        Width = width,
        Height = 12,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>That the control put the named token's ink on the frame, at a horizontal offset into it, halfway down.</summary>
    private static void _AssertInk(string brushKey, Visual control, RenderedScene.Scene scene, double x) =>
        Assert.Equal(RenderedScene.AsRendered(RenderedScene.TokenBrush(brushKey)),
            _PaintedIn(control, scene, x, control.Bounds.Height / 2));

    private static Color _PaintedIn(Visual control, RenderedScene.Scene scene, double x, double y)
    {
        Assert.True(control.IsEffectivelyVisible, "a control that is not on screen keeps its last bounds, and sampling those measures nothing");

        var point = control.TranslatePoint(new Point(x, y), scene.Window)
            ?? throw new InvalidOperationException("the control is not in the window's coordinate space");

        return RenderedScene.PaintedAt(scene.Window, point);
    }
}
