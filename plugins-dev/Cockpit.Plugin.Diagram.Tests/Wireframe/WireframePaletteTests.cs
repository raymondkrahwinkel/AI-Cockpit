using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-903's component picker: every keyword the format has is on it, in a group, with the component itself drawn
// beside the word. A keyword that is only in the enum is one the operator cannot reach.
[Collection("avalonia")]
public class WireframePaletteTests
{
    [Fact]
    public void ThePalette_OffersEveryKeywordExceptScreen_Once()
    {
        var offered = WireframeWorkspaceBody.Palette.SelectMany(group => group.Kinds).ToList();
        var expected = Enum.GetValues<WireframeNodeKind>().Where(kind => kind != WireframeNodeKind.Screen);

        Assert.Equal(offered.Count, offered.Distinct().Count());
        Assert.Equal(expected.ToHashSet(), offered.ToHashSet());
    }

    [Fact]
    public void EveryEntry_IsBuiltWithItsOwnPreviewAndItsKeyword()
    {
        var picked = new List<WireframeNodeKind>();
        var entries = _Entries(WireframeWorkspaceBody.BuildPalette(picked.Add));

        Assert.Equal(WireframeWorkspaceBody.Palette.Sum(group => group.Kinds.Length), entries.Count);
        foreach (var entry in entries)
        {
            var content = Assert.IsType<StackPanel>(entry.Content);
            Assert.IsType<Viewbox>(content.Children[0]);
            Assert.NotEmpty(Assert.IsType<TextBlock>(content.Children[1]).Text!);
        }

        var keywords = entries.Select(entry => ((TextBlock)((StackPanel)entry.Content!).Children[1]).Text).ToList();
        Assert.Equal(
            WireframeWorkspaceBody.Palette.SelectMany(group => group.Kinds).Select(WireframeHandEdit.Keyword),
            keywords);
    }

    private static List<ToggleButton> _Entries(Control root) => root switch
    {
        ToggleButton toggle => [toggle],
        Panel panel => [.. panel.Children.OfType<Control>().SelectMany(_Entries)],
        _ => [],
    };
}
