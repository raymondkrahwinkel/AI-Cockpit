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
    public void Couple_IsExclusive_EvenAgainstAZeroCapabilityCoupling()
    {
        var registry = _Open();
        registry.Couple(Session, SurfaceId);

        Assert.True(registry.IsCoupledByAnother("session-b", SurfaceId));
        Assert.Throws<InvalidOperationException>(() => registry.Couple("session-b", SurfaceId));
        Assert.Throws<InvalidOperationException>(() => registry.Grant("session-b", SurfaceId, WireframeCapability.Read));
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

}
