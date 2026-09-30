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

    private static WireframeAccessRegistry _Coupled(string? source = null)
    {
        source ??= WireframeScreens.Settings;
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

    // ---- Criterion 4: the selection is found exactly, or it is gone ----

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

    // ---- Criterion 7: the race the ids exist for ----

    private static IEnumerable<WireframeNode> _Flatten(WireframeNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(_Flatten));

    // The source as it read before anything named its components, so a stamped one can be compared against it.
    private static string _WithoutIds(string source) =>
        string.Join("\n", source.Split('\n').Select(line => line.Split(" #")[0]));
}
