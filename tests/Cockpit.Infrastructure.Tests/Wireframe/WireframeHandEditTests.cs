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
    private const string Session = "session-a";
    private const string SurfaceId = "wireframe-1";

    private static WireframeAccessRegistry _Open()
    {
        var registry = new WireframeAccessRegistry();
        registry.SurfaceOpened(SurfaceId, "Instellingen", WireframeScreens.Settings);
        return registry;
    }

    private static string _TextOf(WireframeAccessRegistry registry) => registry.PeekText(SurfaceId)!;

    private static IReadOnlyList<WireframeNode> _Tree(WireframeAccessRegistry registry) =>
        WireframeParser.Parse(_TextOf(registry)).Screens;

    // The nav's item wordings, read off the tree rather than counted by hand.
    private static List<string?> _NavItems(WireframeAccessRegistry registry) =>
        WireframeHandEdit.Find(_Tree(registry), WireframeScreens.Nav)!.Children.Select(child => child.Text).ToList();

    // Everything sitting in the same container as the component with this wording, itself included — the way to check
    // what a component left behind once it has moved elsewhere.
    private static List<string?> _SiblingsOf(IReadOnlyList<WireframeNode> screens, string text)
    {
        var node = screens.SelectMany(_Walk).First(candidate => candidate.Text == text);
        return WireframeHandEdit.Placement(screens, node.Id!)!.Value.Parent.Children.Select(child => child.Text).ToList();
    }

    private static IEnumerable<WireframeNode> _Walk(WireframeNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(_Walk));

    [Fact]
    public void EveryHandling_IsJournaledAsTheOperators_SoTheStripShowsBothOrigins()
    {
        var registry = _Open();

        Assert.Null(registry.ApplyHandEdit(SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren")));

        var entry = Assert.Single(registry.History(SurfaceId));
        Assert.Equal("operator", entry.Origin);
        Assert.Equal(WireframeEditKind.SetText, entry.Kind);
        Assert.Contains("Bewaren", entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandling_IsTakenBackOnItsOwn_LikeAnAgentsIs()
    {
        var registry = _Open();
        registry.ApplyHandEdit(SurfaceId, WireframeComponentEdit.Remove(WireframeScreens.EmailField));
        var entry = Assert.Single(registry.History(SurfaceId));

        Assert.Null(registry.Revert(SurfaceId, entry.Id));

        Assert.Equal(WireframeScreens.Settings, _TextOf(registry));
    }

    [Fact]
    public void SetText_KeepsTheModifiersTheComponentAlreadyCarried()
    {
        var registry = _Open();

        registry.ApplyHandEdit(SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));

        Assert.Contains("button \"Bewaren\" primary", _TextOf(registry), StringComparison.Ordinal);
    }

    [Fact]
    public void AddAsChild_LandsInsideTheSelectedContainer()
    {
        var registry = _Open();

        registry.ApplyHandEdit(SurfaceId, WireframeHandEdit.AddChild(WireframeScreens.Nav, "item", "Beveiliging"));

        Assert.Equal(["Algemeen", "Account", "Beveiliging"], _NavItems(registry));
    }

    [Fact]
    public void AddAsSibling_LandsStraightAfterTheSelectedComponent_NotAtTheEnd()
    {
        var registry = _Open();
        var edit = WireframeHandEdit.AddSibling(_Tree(registry), WireframeScreens.GeneralItem, "item", "Beveiliging");

        Assert.Null(registry.ApplyHandEdit(SurfaceId, edit!));

        Assert.Equal(["Algemeen", "Beveiliging", "Account"], _NavItems(registry));
    }

    [Fact]
    public void AddAsSibling_OfTheScreenLine_IsNotOffered()
    {
        var registry = _Open();

        Assert.Null(WireframeHandEdit.AddSibling(_Tree(registry), WireframeScreens.Screen, "row", null));
    }

    // The one an off-by-one gets wrong: stepping down has to clear the neighbour it swaps with, not stop in front of it.
    [Fact]
    public void OneStepDown_SwapsWithTheNeighbourBelow()
    {
        var registry = _Open();
        var edit = WireframeHandEdit.Reorder(_Tree(registry), WireframeScreens.GeneralItem, delta: 1);

        Assert.Null(registry.ApplyHandEdit(SurfaceId, edit!));

        Assert.Equal(["Account", "Algemeen"], _NavItems(registry));
    }

    [Fact]
    public void OneStepUp_SwapsWithTheNeighbourAbove()
    {
        var registry = _Open();
        var edit = WireframeHandEdit.Reorder(_Tree(registry), WireframeScreens.AccountItem, delta: -1);

        Assert.Null(registry.ApplyHandEdit(SurfaceId, edit!));

        Assert.Equal(["Account", "Algemeen"], _NavItems(registry));
    }

    [Theory]
    [InlineData(WireframeScreens.GeneralItem, -1)]
    [InlineData(WireframeScreens.AccountItem, 1)]
    public void AStepPastTheEndOfItsOwnContainer_IsNotOffered(string component, int delta)
    {
        var registry = _Open();

        Assert.Null(WireframeHandEdit.Reorder(_Tree(registry), component, delta));
    }

    [Fact]
    public void ReorderingAContainer_TakesEverythingInsideItAlong()
    {
        var registry = _Open();
        var edit = WireframeHandEdit.Reorder(_Tree(registry), WireframeScreens.LeftColumn, delta: 1);

        Assert.Null(registry.ApplyHandEdit(SurfaceId, edit!));

        // The wide column now comes first, and the nav is still inside the narrow one it belongs to.
        var row = WireframeHandEdit.Find(_Tree(registry), WireframeScreens.Row)!;
        Assert.Equal("3", row.Children[0].ValueOf(WireframeModifierName.W));
        Assert.Equal("1", row.Children[1].ValueOf(WireframeModifierName.W));
        Assert.Equal(WireframeNodeKind.Nav, Assert.Single(row.Children[1].Children).Kind);
    }

    [Fact]
    public void MoveIntoAnotherContainer_TakesTheComponentOutOfTheOldOne()
    {
        var registry = _Open();

        Assert.Null(registry.ApplyHandEdit(
            SurfaceId,
            WireframeComponentEdit.Move(WireframeScreens.SaveButton, WireframeScreens.Nav, position: null)));

        Assert.Equal(["Algemeen", "Account", "Opslaan"], _NavItems(registry));
        Assert.Equal(["Annuleren"], _SiblingsOf(_Tree(registry), "Annuleren"));
    }

    [Fact]
    public void Destinations_LeaveOutTheComponentItselfAndWhatItAlreadyContains()
    {
        var registry = _Open();

        var destinations = WireframeHandEdit.Destinations(_Tree(registry), WireframeScreens.LeftColumn).Select(node => node.Id).ToList();

        Assert.DoesNotContain(WireframeScreens.LeftColumn, destinations);
        Assert.DoesNotContain(WireframeScreens.Nav, destinations);
        Assert.Contains(WireframeScreens.Group, destinations);
    }

    [Fact]
    public void Destinations_LeaveOutTheContainerItIsAlreadyTheLastChildOf()
    {
        var registry = _Open();

        var destinations = WireframeHandEdit.Destinations(_Tree(registry), WireframeScreens.SaveButton).Select(node => node.Id).ToList();

        Assert.DoesNotContain(WireframeScreens.ButtonRow, destinations);
        Assert.Contains(WireframeScreens.Nav, destinations);
    }

    [Fact]
    public void RemovingTheScreenLine_IsRefusedWithAReason_AndChangesNothing()
    {
        var registry = _Open();

        var refusal = registry.ApplyHandEdit(SurfaceId, WireframeComponentEdit.Remove(WireframeScreens.Screen));

        Assert.NotNull(refusal);
        Assert.Equal(WireframeScreens.Settings, _TextOf(registry));
        Assert.Empty(registry.History(SurfaceId));
    }

    [Fact]
    public void AHandlingOnAClosedSurface_SaysSoRatherThanThrowing()
    {
        var registry = _Open();
        registry.SurfaceClosed(SurfaceId);

        Assert.Equal("Dit wireframe staat niet meer open.", registry.ApplyHandEdit(SurfaceId, WireframeComponentEdit.Remove(WireframeScreens.Row)));
    }

    // The hold exists to keep the agent off what the operator has under their hand, not to lock the operator out of
    // their own selection — selecting a component is what takes that hold in the first place.
    [Fact]
    public void TheOperatorsOwnHold_DoesNotBlockTheirOwnHandling()
    {
        var registry = _Open();
        registry.HoldComponent(SurfaceId, WireframeScreens.SaveButton);

        Assert.Null(registry.ApplyHandEdit(SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren")));
    }

    [Fact]
    public void WhileTheOperatorHoldsOneComponent_AnAgentEditOnAnotherStillLands()
    {
        var registry = _Open();
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);
        registry.HoldComponent(SurfaceId, WireframeScreens.SaveButton);

        var refused = registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));
        var landed = registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.NameField, "Naam"));

        Assert.NotNull(refused.Refusal);
        Assert.Null(landed.Refusal);
        Assert.Contains("button \"Opslaan\" primary", _TextOf(registry), StringComparison.Ordinal);
        Assert.Contains("input \"Naam\"", _TextOf(registry), StringComparison.Ordinal);
    }

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
