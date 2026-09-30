using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Infrastructure.Wireframe;

namespace Cockpit.Infrastructure.Tests.Wireframe;

/// <summary>
/// The safety net that stands in for the diff gate on this surface (AC-872, AC-853): every handling is journaled and
/// every kind of handling can be taken back on its own, found by the lines it wrote rather than by a line number a
/// later edit has moved. Plus AC-841's hold: a call on a component the operator has under their hand is refused.
/// </summary>
public class WireframeUndoTests
{
    private const string Session = "session-a";
    private const string SurfaceId = "wireframe-1";

    private static WireframeAccessRegistry _Coupled()
    {
        var registry = new WireframeAccessRegistry();
        registry.SurfaceOpened(SurfaceId, "Instellingen", WireframeScreens.Settings);
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);
        return registry;
    }

    // One journal line per handling, and every kind of handling taken back on its own — found by the lines it wrote
    // rather than by a line number a later edit has moved, so each of these ends back at the source it started from.
    public static IEnumerable<object[]> JournaledEdits() =>
    [
        [WireframeComponentEdit.Add(WireframeScreens.Group, "input", "Telefoonnummer", null, null), WireframeEditKind.Add],
        [WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"), WireframeEditKind.SetText],
        [WireframeComponentEdit.Remove(WireframeScreens.LeftColumn), WireframeEditKind.Remove],
        [WireframeComponentEdit.Move(WireframeScreens.SaveButton, WireframeScreens.Group, position: 0), WireframeEditKind.Move],
        [WireframeComponentEdit.SetViewport(WireframeViewport.Mobile), WireframeEditKind.SetViewport],
    ];

    // AC-841: the operator's hold covers whichever end of the call names the held component — the component being
    // changed, the container something is added into, and either end of a move.
    public static IEnumerable<object[]> EditsTouchingTheHeldComponent() =>
    [
        [WireframeScreens.SaveButton, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren")],
        [WireframeScreens.Group, WireframeComponentEdit.Add(WireframeScreens.Group, "input", "Telefoon", null, null)],
        [WireframeScreens.Group, WireframeComponentEdit.Move(WireframeScreens.SaveButton, WireframeScreens.Group, null)],
    ];

    [Theory]
    [MemberData(nameof(EditsTouchingTheHeldComponent))]
    public void AnEditTouchingAComponentTheOperatorIsHolding_IsRefusedWithAReason_NotSwallowed(
        string held,
        WireframeComponentEdit edit)
    {
        var registry = _Coupled();
        registry.HoldComponent(SurfaceId, held);

        var result = registry.EditCoupled(Session, SurfaceId, edit);

        Assert.Contains($"editing the component with id \"{held}\" right now", result.Refusal);
        Assert.Equal(WireframeScreens.Settings, registry.PeekText(SurfaceId));
        Assert.Empty(registry.History(SurfaceId));
    }

}
