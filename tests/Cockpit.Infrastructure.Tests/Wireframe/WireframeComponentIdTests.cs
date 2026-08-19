using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Infrastructure.Wireframe;

namespace Cockpit.Infrastructure.Tests.Wireframe;

/// <summary>
/// AC-906: a component is named by an id that lives in the source and outlives every line number. Covers what that
/// buys — identity through other people's edits, a refusal instead of a near miss, and a hold on the component.
/// </summary>
public class WireframeComponentIdTests
{
    private const string Session = "session-a";
    private const string SurfaceId = "wireframe-1";
    private const string Name = "Instellingen";

    private static WireframeAccessRegistry _Coupled(string source = WireframeScreens.Settings)
    {
        var registry = new WireframeAccessRegistry();
        registry.SurfaceOpened(SurfaceId, Name, source);
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);
        return registry;
    }

    private static WireframeNode _Tree(WireframeAccessRegistry registry) =>
        WireframeParser.Parse(registry.PeekText(SurfaceId)!).Screens.SingleOrDefault()!;

    private static WireframeNode? _Component(WireframeAccessRegistry registry, string id) =>
        WireframeHandEdit.Find(_Tree(registry), id);

    // ---- Criterion 1: identity survives what happens around it ----

    [Fact]
    public void AnId_StaysWithItsComponent_ThroughEveryChangeToTheOnesAroundIt()
    {
        var registry = _Coupled();

        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Add(WireframeScreens.Nav, "item", "Beveiliging", null, position: 0));
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Add(WireframeScreens.Nav, "item", "Sneltoetsen", null, null));
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Remove(WireframeScreens.EmailField));
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.NameField, "Volledige naam"));
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Move(WireframeScreens.Nav, WireframeScreens.Group, position: 0));

        var save = _Component(registry, WireframeScreens.SaveButton);
        Assert.Equal("Opslaan", save?.Text);
        Assert.True(save!.Has(WireframeModifierName.Primary));
        Assert.NotEqual(WireframeScreens.SaveButtonLine, save.Line);
    }

    // ---- Criterion 2: a name that no longer exists is a refusal ----

    [Fact]
    public void AnIdThatNamesNothingAnyMore_IsRefusedWithAReason_NotAppliedToWhateverTookItsPlace()
    {
        var registry = _Coupled();
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Remove(WireframeScreens.SaveButton));

        var result = registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));

        Assert.Equal("This wireframe has no component with id \"save\" — it may have been removed. Read it again for the ids as they now stand.", result.Refusal);
        Assert.Contains("button \"Annuleren\" #cancel", registry.PeekText(SurfaceId)!, StringComparison.Ordinal);
    }

    // ---- Criterion 3: the hold protects the component, not the line ----

    [Fact]
    public void TheHold_FollowsTheComponent_WhenSomethingIsAddedAboveIt()
    {
        var registry = _Coupled();
        registry.HoldComponent(SurfaceId, WireframeScreens.SaveButton);
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Add(WireframeScreens.Nav, "item", "Beveiliging", null, position: 0));

        // The held button has moved down a line, and its old line now holds the one above it.
        Assert.Equal(WireframeScreens.SaveButtonLine + 1, _Component(registry, WireframeScreens.SaveButton)!.Line);

        var held = registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));
        var free = registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText("cancel", "Terug"));

        Assert.Contains("editing the component with id \"save\"", held.Refusal);
        Assert.Null(free.Refusal);
    }

    // ---- Criterion 4: the selection is found exactly, or it is gone ----

    [Fact]
    public void AComponentThatWasRemoved_IsNotFoundBack_RatherThanResolvingToItsNeighbour()
    {
        var registry = _Coupled();
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.Remove(WireframeScreens.SaveButton));

        Assert.Null(_Component(registry, WireframeScreens.SaveButton));
        Assert.Equal("Annuleren", _Component(registry, "cancel")?.Text);
    }

    // ---- Criteria 5 and 6: a source without ids keeps working, and a read is what names it ----

    [Fact]
    public void ASourceWithoutIds_IsLeftAlone_UntilAnAgentReadsIt()
    {
        var registry = _Coupled(WireframeScreens.Plain);

        Assert.Equal(WireframeScreens.Plain, registry.PeekText(SurfaceId));

        var read = registry.ReadCoupled(Session, SurfaceId);

        Assert.Equal(read, registry.PeekText(SurfaceId));
        Assert.All(_Flatten(_Tree(registry)), component => Assert.NotNull(component.Id));
        Assert.Equal(WireframeScreens.Plain, _WithoutIds(read!));
    }

    [Fact]
    public void Ensure_KeepsTheIdsSomeoneAlreadyChose_AndNeverHandsOutTheSameOneTwice()
    {
        var stamped = WireframeComponentIds.Ensure("screen \"X\" #c1\n  button \"Opslaan\"\n  button \"Annuleren\" #save");

        Assert.Equal("screen \"X\" #c1\n  button \"Opslaan\" #c2\n  button \"Annuleren\" #save", stamped);
    }

    [Fact]
    public void EnsureComponentId_NamesTheComponentOnThatLine_AndAnnouncesTheStampedSource()
    {
        var registry = _Coupled(WireframeScreens.Plain);
        var announced = new List<string>();
        registry.TextChanged += (_, text) => announced.Add(text);

        var id = registry.EnsureComponentId(SurfaceId, WireframeScreens.SaveButtonLine);

        Assert.Equal("Opslaan", _Component(registry, id!)?.Text);
        Assert.Equal(registry.PeekText(SurfaceId), Assert.Single(announced));
        Assert.Null(registry.EnsureComponentId(SurfaceId, line: 99));
    }

    // ---- Criterion 7: the race the ids exist for ----

    [Fact]
    public void ReadThenTheOperatorAddsAbove_TheAgentsCallStillHitsTheComponentItRead()
    {
        // The old line-numbered call would have landed on "Annuleren", which is what slid into line 13 meanwhile.
        var registry = _Coupled(WireframeScreens.Plain);
        registry.ReadCoupled(Session, SurfaceId);
        var asRead = _Tree(registry);
        var save = WireframeHandEdit.Find(asRead, WireframeScreens.SaveButtonLine)!.Id!;
        var nav = WireframeHandEdit.Find(asRead, line: 4)!.Id!;

        registry.ApplyHandEdit(SurfaceId, WireframeHandEdit.AddChild(nav, "item", "Beveiliging"));
        Assert.Equal("Annuleren", WireframeHandEdit.Find(_Tree(registry), WireframeScreens.SaveButtonLine)?.Text);

        var result = registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(save, "Bewaren"));

        Assert.Null(result.Refusal);
        Assert.Equal("Bewaren", _Component(registry, save)?.Text);
        Assert.Contains("button \"Annuleren\"", registry.PeekText(SurfaceId)!, StringComparison.Ordinal);
    }

    private static IEnumerable<WireframeNode> _Flatten(WireframeNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(_Flatten));

    // The source as it read before anything named its components, so a stamped one can be compared against it.
    private static string _WithoutIds(string source) =>
        string.Join("\n", source.Split('\n').Select(line => line.Split(" #")[0]));
}
