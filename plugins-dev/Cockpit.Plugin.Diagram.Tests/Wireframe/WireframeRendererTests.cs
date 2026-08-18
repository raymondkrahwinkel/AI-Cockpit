using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// What the renderer promises: every source line ends up as a control that points back at it, weights become
// star-sized tracks, and the whole thing actually paints. No assertion here is nailed to a pixel position.
[Collection("avalonia")]
public class WireframeRendererTests
{
    public static TheoryData<string> Screens => WireframeScreens.Names;

    [Theory]
    [MemberData(nameof(Screens))]
    public void EveryNode_IsCarriedByExactlyOneControl(string screen)
    {
        var root = WireframeParser.Parse(WireframeScreens.Source(screen)).Root;
        Assert.NotNull(root);

        var control = _Arrange(root);
        var carried = _Controls(control)
            .Select(WireframeSource.GetNode)
            .Where(node => node is not null)
            .ToList();

        Assert.Equal(_Nodes(root).Count, carried.Count);
        Assert.Equal(_Nodes(root).ToHashSet(), carried.ToHashSet()!);
    }

    [Fact]
    public void AnUnselectedTab_IsDrawnButHidden_SoNoLineLosesItsControl()
    {
        var root = WireframeParser.Parse("""
            screen "X"
              tabs
                tab "Eerste"
                  label "Een"
                tab "Tweede" selected
                  label "Twee"
            """).Root;
        Assert.NotNull(root);

        var control = _Arrange(root);
        var tabs = root.Children.Single().Children;

        Assert.False(_ControlFor(control, tabs[0]).IsVisible);
        Assert.True(_ControlFor(control, tabs[1]).IsVisible);
        Assert.Equal("Een", _ControlFor(control, tabs[0].Children.Single()).GetValue(TextBlock.TextProperty));
    }

    [Fact]
    public void Weights_BecomeStarSizedTracks_AndTheRestSizesToItsContent()
    {
        var root = WireframeParser.Parse("""
            screen "X"
              row
                column w:1
                column w:3
                label "Vast"
            """).Root;
        Assert.NotNull(root);

        var control = _Arrange(root);
        var row = Assert.IsType<Grid>(_ControlFor(control, root.Children.Single()));

        Assert.Equal(new GridLength(1, GridUnitType.Star), row.ColumnDefinitions[0].Width);
        Assert.Equal(new GridLength(3, GridUnitType.Star), row.ColumnDefinitions[1].Width);
        Assert.Equal(GridLength.Auto, row.ColumnDefinitions[2].Width);
    }

    [Fact]
    public void Disabled_DimsTheControl_AndAlignPushesIt()
    {
        var root = WireframeParser.Parse("""
            screen "X"
              button "Weg" disabled
              button "Opslaan" align:right
            """).Root;
        Assert.NotNull(root);

        var control = _Arrange(root);

        Assert.Equal(0.45, _ControlFor(control, root.Children[0]).Opacity);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, _ControlFor(control, root.Children[1]).HorizontalAlignment);
    }

    // AC-903's wireframe look: text nobody has written yet is room on the page, not an empty control.
    [Fact]
    public void AComponentWithoutText_IsDrawnAsPlaceholderLines()
    {
        var root = WireframeParser.Parse("""
            screen "X"
              label
              label "Echte tekst"
            """).Root;
        Assert.NotNull(root);

        var control = _Arrange(root);
        var lines = Assert.IsType<StackPanel>(_ControlFor(control, root.Children[0]));

        Assert.Equal(2, lines.Children.Count);
        Assert.All(lines.Children, line => Assert.Equal(8, Assert.IsType<Border>(line).Height));
        Assert.IsType<TextBlock>(_ControlFor(control, root.Children[1]));
    }

    [Fact]
    public void Progress_SplitsItsTrackAtTheValue()
    {
        var root = WireframeParser.Parse("screen \"X\"\n  progress value:60").Root;
        Assert.NotNull(root);

        var track = Assert.IsType<Grid>(_ControlFor(_Arrange(root), root.Children.Single()));

        Assert.Equal(new GridLength(60, GridUnitType.Star), track.ColumnDefinitions[0].Width);
        Assert.Equal(new GridLength(40, GridUnitType.Star), track.ColumnDefinitions[1].Width);
    }

    // A dialog written under the screen is drawn over it, not as a band between the components above and below it —
    // which is the only way the screen under it stays readable.
    [Fact]
    public void AModalUnderTheScreen_CoversItInsteadOfTakingABandOfItsOwn()
    {
        var root = WireframeParser.Parse("""
            screen "X"
              label "Eronder"
              modal "Weet je het zeker?"
                button "Ja"
            """).Root;
        Assert.NotNull(root);

        var control = _Arrange(root);
        var rows = _ControlFor(control, root.Children[0]).Parent;
        var modal = _ControlFor(control, root.Children[1]);

        Assert.IsType<Grid>(rows);
        Assert.NotSame(rows, modal.Parent);
        Assert.Same(rows.Parent, modal.Parent);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void EachScreen_ActuallyPaints(string screen)
    {
        var root = WireframeParser.Parse(WireframeScreens.Source(screen)).Root;
        Assert.NotNull(root);

        var colours = _Paint(root, screen);

        Assert.True(colours.Count > 1, $"'{screen}' schilderde één vlakke kleur — er is niets getekend");
        Assert.Contains(Colors.White, colours);
    }

    // Renders to a real raster so the drawing can be looked at, not only reasoned about (Iron Law #9). Set
    // COCKPIT_WIREFRAME_RENDERS to a directory and the PNGs land there.
    private static HashSet<Color> _Paint(WireframeNode root, string name)
    {
        var size = new PixelSize(900, 620);
        var control = WireframeRenderer.Render(root);
        control.Measure(new Size(size.Width, size.Height));
        control.Arrange(new Rect(0, 0, size.Width, size.Height));

        using var target = new RenderTargetBitmap(size);
        target.Render(control);

        var directory = Environment.GetEnvironmentVariable("COCKPIT_WIREFRAME_RENDERS");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            target.Save(Path.Combine(directory, $"{name}.png"), PngBitmapEncoderOptions.Default);
        }

        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;

        using var decoded = WriteableBitmap.Decode(stream);
        using var buffer = decoded.Lock();
        var row = new byte[buffer.RowBytes];
        var colours = new HashSet<Color>();
        for (var y = 0; y < size.Height; y++)
        {
            Marshal.Copy(buffer.Address + (y * buffer.RowBytes), row, 0, row.Length);
            for (var x = 0; x < size.Width; x++)
            {
                colours.Add(Color.FromRgb(row[(x * 4) + 2], row[(x * 4) + 1], row[x * 4]));
            }
        }

        return colours;
    }

    private static Control _Arrange(WireframeNode root)
    {
        var control = WireframeRenderer.Render(root);
        control.Measure(new Size(900, 620));
        control.Arrange(new Rect(0, 0, 900, 620));
        return control;
    }

    private static Control _ControlFor(Control root, WireframeNode node) =>
        _Controls(root).Single(control => ReferenceEquals(WireframeSource.GetNode(control), node));

    private static List<Control> _Controls(Control root) =>
        [root, .. root.GetVisualDescendants().OfType<Control>()];

    private static List<WireframeNode> _Nodes(WireframeNode node) =>
        [node, .. node.Children.SelectMany(_Nodes)];
}
