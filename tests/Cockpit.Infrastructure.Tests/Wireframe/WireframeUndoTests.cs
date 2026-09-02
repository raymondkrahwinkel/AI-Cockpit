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

    private static string _Revert(WireframeAccessRegistry registry, int index = 0)
    {
        var entry = registry.History(SurfaceId)[index];
        Assert.Null(registry.Revert(SurfaceId, entry.Id));
        return registry.PeekText(SurfaceId)!;
    }

    [Fact]
    public void Replace_IsJournaled_AndTakenBackWhole()
    {
        var registry = _Coupled();

        var result = registry.WriteCoupled(Session, SurfaceId, "screen \"Leeg\"");
        Assert.Null(result.Refusal);

        var entry = Assert.Single(registry.History(SurfaceId));
        Assert.Equal(WireframeEditKind.Replace, entry.Kind);
        Assert.Equal(Session, entry.Origin);
        Assert.Equal(WireframeScreens.Settings, _Revert(registry));
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

    [Theory]
    [MemberData(nameof(JournaledEdits))]
    public void EveryKindOfEdit_IsJournaledUnderItsOwnKind_AndTakenBackToTheSourceItStartedFrom(
        WireframeComponentEdit edit,
        WireframeEditKind kind)
    {
        var registry = _Coupled();
        registry.EditCoupled(Session, SurfaceId, edit);

        Assert.Equal(kind, Assert.Single(registry.History(SurfaceId)).Kind);
        Assert.Equal(WireframeScreens.Settings, _Revert(registry));
    }

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

    [Fact]
    public void Revert_ReachesAnOlderEdit_ThroughALaterOneMadeSomewhereElse()
    {
        // The whole point of journaling per handling rather than keeping one "previous source": an edit further down
        // the screen moved the older edit's lines, and it is still found — by what it wrote, not by where it was.
        var registry = _Coupled();
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Add(WireframeScreens.Nav, "item", "Beveiliging", null, position: 0));

        var text = _Revert(registry);

        Assert.Contains("button \"Opslaan\" primary", text, StringComparison.Ordinal);
        Assert.Contains("item \"Beveiliging\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_Twice_SaysSoRatherThanUndoingSomethingElse()
    {
        var registry = _Coupled();
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));
        var entry = Assert.Single(registry.History(SurfaceId));

        Assert.Null(registry.Revert(SurfaceId, entry.Id));

        Assert.Equal("This edit has already been reverted.", registry.Revert(SurfaceId, entry.Id));
        Assert.True(Assert.Single(registry.History(SurfaceId)).Reverted);
    }

    [Fact]
    public void Revert_OfAnEditWhoseLinesAreGone_IsRefusedWithAReason_RatherThanGuessing()
    {
        var registry = _Coupled();
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));
        var entry = Assert.Single(registry.History(SurfaceId));
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Remove(WireframeScreens.SaveButton));

        var refusal = registry.Revert(SurfaceId, entry.Id);

        Assert.Equal("This edit can no longer be found in the wireframe.", refusal);
    }

    [Fact]
    public void Revert_OfAnUnknownEntry_IsRefused()
    {
        var registry = _Coupled();

        Assert.Equal("This edit was not found.", registry.Revert(SurfaceId, "no-such-entry"));
    }

    [Fact]
    public void History_ShowsTheHandlingsOldestFirst_WithASummaryPerLine()
    {
        var registry = _Coupled();
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Remove(WireframeScreens.EmailField));

        var history = registry.History(SurfaceId);

        Assert.Equal(2, history.Count);
        Assert.Contains("Bewaren", history[0].Summary, StringComparison.Ordinal);
        Assert.Contains("removed input", history[1].Summary, StringComparison.Ordinal);
        Assert.Equal(WireframeScreens.SaveButton, history[0].ComponentKey);
    }

    [Fact]
    public void SetText_OnAScreenWithAFlowPointingAtIt_IsOneStep_AndUndoRestoresTitleAndReferences()
    {
        // AC-902 AC4: the rename and every goto: it carries along are one journaled edit, so reverting it puts the
        // title and the reference back together rather than leaving one behind.
        var registry = new WireframeAccessRegistry();
        registry.SurfaceOpened(SurfaceId, "Aanmelden", WireframeScreens.TwoScreensWithFlow);
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);

        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SignupScreen, "Account aanmaken"));

        Assert.Equal(WireframeEditKind.SetText, Assert.Single(registry.History(SurfaceId)).Kind);
        var reverted = _Revert(registry);
        Assert.Equal(WireframeScreens.TwoScreensWithFlow, reverted);
    }

    [Fact]
    public void ReleaseComponent_LetsTheSameCallThrough()
    {
        var registry = _Coupled();
        registry.HoldComponent(SurfaceId, WireframeScreens.SaveButton);
        registry.ReleaseComponent(SurfaceId, WireframeScreens.SaveButton);

        Assert.False(registry.IsHeldByOperator(SurfaceId, WireframeScreens.SaveButton));
        Assert.Null(registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren")).Refusal);
    }
}
