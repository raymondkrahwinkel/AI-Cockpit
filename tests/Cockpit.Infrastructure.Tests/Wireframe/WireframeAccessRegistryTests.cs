using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Infrastructure.Wireframe;

namespace Cockpit.Infrastructure.Tests.Wireframe;

/// <summary>
/// The coupling rules behind the wireframe-access MCP (AC-872), the same contract its two neighbours carry: a
/// coupling can exist with zero capabilities, Edit implies Read, one agent per surface, and a close or a session end
/// decouples on its own.
/// </summary>
public class WireframeAccessRegistryTests
{
    private const string Session = "session-a";
    private const string SurfaceId = "wireframe-1";
    private const string Name = "Instellingen";

    private static WireframeAccessRegistry _Open()
    {
        var registry = new WireframeAccessRegistry();
        registry.SurfaceOpened(SurfaceId, Name, WireframeScreens.Settings);
        return registry;
    }

    [Fact]
    public void Couple_OnItsOwn_GrantsNoCapabilities()
    {
        var registry = _Open();

        registry.Couple(Session, SurfaceId);

        var coupling = registry.CouplingOf(Session, SurfaceId);
        Assert.NotNull(coupling);
        Assert.False(coupling.CanRead);
        Assert.False(coupling.CanEdit);
        Assert.False(coupling.HasAnyCapability);
        Assert.Null(registry.ReadCoupled(Session, SurfaceId));
        Assert.NotNull(registry.WriteCoupled(Session, SurfaceId, WireframeScreens.Settings).Refusal);
    }

    [Fact]
    public void ReadCoupled_NeedsRead_AndThenReturnsTheSurfaceAsItStandsNow()
    {
        var registry = _Open();

        Assert.Null(registry.ReadCoupled(Session, SurfaceId));

        registry.Grant(Session, SurfaceId, WireframeCapability.Read);

        Assert.Equal(WireframeScreens.Settings, registry.ReadCoupled(Session, SurfaceId));
    }

    [Fact]
    public void Grant_Edit_AlsoGrantsRead_ButNotTheOtherWayRound()
    {
        var registry = _Open();

        registry.Grant(Session, SurfaceId, WireframeCapability.Read);
        Assert.False(registry.CouplingOf(Session, SurfaceId)!.CanEdit);

        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);

        var coupling = registry.CouplingOf(Session, SurfaceId);
        Assert.True(coupling!.CanRead);
        Assert.True(coupling.CanEdit);
    }

    [Fact]
    public void Grant_Read_AfterEdit_DoesNotNarrowTheCoupling()
    {
        var registry = _Open();
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);

        registry.Grant(Session, SurfaceId, WireframeCapability.Read);

        Assert.True(registry.CouplingOf(Session, SurfaceId)!.CanEdit);
    }

    [Fact]
    public void Couple_IsExclusive_EvenAgainstAZeroCapabilityCoupling()
    {
        var registry = _Open();
        registry.Couple(Session, SurfaceId);

        Assert.True(registry.IsCoupledByAnother("session-b", SurfaceId));
        Assert.Throws<InvalidOperationException>(() => registry.Couple("session-b", SurfaceId));
        Assert.Throws<InvalidOperationException>(() => registry.Grant("session-b", SurfaceId, WireframeCapability.Read));
    }

    [Fact]
    public void Couple_OnASurfaceThatIsNotOpen_Throws()
    {
        var registry = new WireframeAccessRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.Couple(Session, "nothing-here"));
    }

    [Fact]
    public void SessionEnded_DropsEveryCouplingThatSessionHeld_AndAnnouncesEachOne()
    {
        var registry = _Open();
        registry.SurfaceOpened("wireframe-2", "Overzicht", WireframeScreens.Settings);
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);
        registry.Couple(Session, "wireframe-2");
        var announced = new List<WireframeCouplingChange>();
        registry.CouplingChanged += announced.Add;

        registry.SessionEnded(Session);

        Assert.Null(registry.CouplingOf(Session, SurfaceId));
        Assert.Null(registry.CouplingOf(Session, "wireframe-2"));
        Assert.Equal(2, announced.Count);
        Assert.All(announced, change => Assert.Null(change.Coupling));
        Assert.False(registry.IsCoupledByAnother("session-b", SurfaceId));
    }

    [Fact]
    public void SurfaceClosed_BreaksTheCoupling_AndForgetsTheHistory()
    {
        var registry = _Open();
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);
        registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));

        registry.SurfaceClosed(SurfaceId);

        Assert.Null(registry.CouplingOf(Session, SurfaceId));
        Assert.Empty(registry.History(SurfaceId));
        Assert.Null(registry.Resolve(Name));
    }

    [Fact]
    public void Disconnect_BreaksTheCouplingAtOnce_WhateverItHeld()
    {
        var registry = _Open();
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);

        registry.Disconnect(SurfaceId);

        Assert.Null(registry.CouplingOf(Session, SurfaceId));
        Assert.Null(registry.ReadCoupled(Session, SurfaceId));
    }

    [Fact]
    public void MarkRead_StampsTheCoupling_ButOnlyWhenItActuallyHoldsRead()
    {
        var registry = _Open();
        registry.Couple(Session, SurfaceId);

        registry.MarkRead(Session, SurfaceId);
        Assert.Null(registry.CouplingOf(Session, SurfaceId)!.LastReadAt);

        registry.Grant(Session, SurfaceId, WireframeCapability.Read);
        registry.MarkRead(Session, SurfaceId);

        Assert.NotNull(registry.CouplingOf(Session, SurfaceId)!.LastReadAt);
    }

    [Fact]
    public void UpdateText_KeepsTheRegistrysCopyInStepWithTheOperatorsOwnEditing()
    {
        var registry = _Open();
        registry.Grant(Session, SurfaceId, WireframeCapability.Read);
        var announced = new List<string>();
        registry.TextChanged += (_, text) => announced.Add(text);

        registry.UpdateText(SurfaceId, "screen \"Iets anders\"");

        Assert.Equal("screen \"Iets anders\"", Assert.Single(announced));
        Assert.Equal("screen \"Iets anders\" #c1", registry.ReadCoupled(Session, SurfaceId));
    }

    [Fact]
    public void WriteCoupled_RefusesASourceTheFormatCannotReadBack()
    {
        var registry = _Open();
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);

        var result = registry.WriteCoupled(Session, SurfaceId, "button \"Los\"");

        Assert.NotNull(result.Refusal);
        Assert.Equal(WireframeScreens.Settings, registry.PeekText(SurfaceId));
    }

    [Fact]
    public void EditCoupled_WithoutEdit_IsRefused_AndChangesNothing()
    {
        var registry = _Open();
        registry.Grant(Session, SurfaceId, WireframeCapability.Read);

        var result = registry.EditCoupled(Session, SurfaceId, WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));

        Assert.NotNull(result.Refusal);
        Assert.Equal(WireframeScreens.Settings, registry.PeekText(SurfaceId));
        Assert.Empty(registry.History(SurfaceId));
    }

    [Fact]
    public void PeekText_HandsTheSourceOverWithoutACoupling_ForTheHostsOwnConsentPrompt()
    {
        var registry = _Open();

        Assert.Equal(WireframeScreens.Settings, registry.PeekText(SurfaceId));
        Assert.Null(registry.PeekText("nothing-here"));
    }

    [Fact]
    public void Resolve_FindsASurfaceByItsIdOrByTheNameTheOperatorSees()
    {
        var registry = _Open();

        Assert.Equal(SurfaceId, registry.Resolve(SurfaceId)!.SurfaceId);
        Assert.Equal(SurfaceId, registry.Resolve(Name)!.SurfaceId);
        Assert.Null(registry.Resolve("Iets anders"));
    }

    [Fact]
    public void ListSurfaces_ShowsEachSessionOnlyItsOwnCoupling()
    {
        var registry = _Open();
        registry.Grant(Session, SurfaceId, WireframeCapability.Edit);

        Assert.True(Assert.Single(registry.ListSurfaces(Session)).Coupling!.CanEdit);
        Assert.Null(Assert.Single(registry.ListSurfaces("session-b")).Coupling);
    }
}
