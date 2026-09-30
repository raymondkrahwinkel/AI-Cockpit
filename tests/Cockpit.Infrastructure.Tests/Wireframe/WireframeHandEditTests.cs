using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Infrastructure.Wireframe;

namespace Cockpit.Infrastructure.Tests.Wireframe;

/// <summary>
/// AC-875: the operator's own handling on the surface. Runs the gestures the panel offers through the registry and
/// checks the source that comes out, so the placement arithmetic and the line surgery are tested together rather than
/// each against the other's assumptions.
/// </summary>
public class WireframeHandEditTests
{
    private const string SurfaceId = "wireframe-1";

    private static WireframeAccessRegistry _Open()
    {
        var registry = new WireframeAccessRegistry();
        registry.SurfaceOpened(SurfaceId, "Instellingen", WireframeScreens.Settings);
        return registry;
    }

    private static IEnumerable<WireframeNode> _Walk(WireframeNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(_Walk));

    // One handling reaches the registry as one change: an agent reading in between sees the source before or after it,
    // never a state where the component has been taken out but not put back.
    [Fact]
    public void OneHandling_RaisesOneTextChange_WithASourceThatParses()
    {
        var registry = _Open();
        var seen = new List<string>();
        registry.TextChanged += (_, text) => seen.Add(text);

        registry.ApplyHandEdit(
            SurfaceId,
            WireframeComponentEdit.Move(WireframeScreens.SaveButton, WireframeScreens.Nav, position: null));

        Assert.NotNull(WireframeParser.Parse(Assert.Single(seen)).Screens.SingleOrDefault());
        Assert.Single(registry.History(SurfaceId));
    }
}
