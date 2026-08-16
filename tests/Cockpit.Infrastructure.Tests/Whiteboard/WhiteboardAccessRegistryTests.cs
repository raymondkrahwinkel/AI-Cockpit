using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Infrastructure.Whiteboard;

namespace Cockpit.Infrastructure.Tests.Whiteboard;

/// <summary>
/// The coupling rules behind the whiteboard-access MCP (AC-823): one capability only (Read), a coupling can exist
/// with it not yet granted, one agent per surface, and a surface close or a session end decouples on its own.
/// Mirrors DiagramAccessRegistryTests (AC-810).
/// </summary>
public class WhiteboardAccessRegistryTests
{
    private static readonly byte[] Png = [1, 2, 3];

    [Fact]
    public void Couple_OnItsOwn_GrantsNoCapability()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);

        registry.Couple("session-a", "surface-1");

        var coupling = registry.CouplingOf("session-a", "surface-1");
        Assert.NotNull(coupling);
        Assert.False(coupling!.CanRead);
        Assert.Null(registry.ReadCoupled("session-a", "surface-1"));
    }

    [Fact]
    public void ReadCoupled_ReturnsTheSnapshotAsItStandsNow()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);

        registry.Couple("session-a", "surface-1");
        registry.Grant("session-a", "surface-1");

        Assert.Equal(Png, registry.ReadCoupled("session-a", "surface-1"));
    }

    [Fact]
    public void Couple_IsExclusive_ASecondAgentIsRefused_EvenAgainstAZeroCapabilityCoupling()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Couple("session-a", "surface-1");

        Assert.True(registry.IsCoupledByAnother("session-b", "surface-1"));
        Assert.Null(registry.CouplingOf("session-b", "surface-1"));
        var act = () => registry.Couple("session-b", "surface-1");
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void PeekSnapshot_ReadsRegardlessOfCoupling_SoTheConsentPromptCanNameWhatIsBeingShared()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);

        Assert.Equal(Png, registry.PeekSnapshot("surface-1"));
        Assert.Null(registry.PeekSnapshot("no-such-surface"));
    }

    [Fact]
    public void SurfaceClosed_DecouplesAutomatically()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("session-a", "surface-1");

        registry.SurfaceClosed("surface-1");

        Assert.Null(registry.CouplingOf("session-a", "surface-1"));
        Assert.Null(registry.PeekSnapshot("surface-1"));
    }

    [Fact]
    public void SessionEnded_DecouplesEverySurfaceThatSessionHeld()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Board one", Png);
        registry.SurfaceOpened("surface-2", "Board two", Png);
        registry.Grant("session-a", "surface-1");
        registry.Grant("session-a", "surface-2");

        registry.SessionEnded("session-a");

        Assert.Null(registry.CouplingOf("session-a", "surface-1"));
        Assert.Null(registry.CouplingOf("session-a", "surface-2"));
    }

    [Fact]
    public void Resolve_MatchesByIdOrByOperatorFacingName()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);

        Assert.Equal("Sprint planning", registry.Resolve("surface-1")!.Name);
        Assert.Equal("surface-1", registry.Resolve("Sprint planning")!.SurfaceId);
        Assert.Null(registry.Resolve("nope"));
    }

    [Fact]
    public void Disconnect_DecouplesWhateverWasHeld_AndAnnounces()
    {
        var registry = new WhiteboardAccessRegistry();
        var changes = new List<WhiteboardCouplingChange>();
        registry.CouplingChanged += changes.Add;
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("session-a", "surface-1");

        registry.Disconnect("surface-1");

        Assert.Null(registry.CouplingOf("session-a", "surface-1"));
        Assert.Equal(2, changes.Count);
        Assert.NotNull(changes[0].Coupling);
        Assert.Null(changes[1].Coupling);
    }

    [Fact]
    public void Grant_RefusesASurfaceThatIsNotOpen()
    {
        var registry = new WhiteboardAccessRegistry();
        var act = () => registry.Grant("session-a", "never-registered");
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void UpdateSnapshot_KeepsWhatAnAgentReadsInStep_AndRaisesSnapshotChanged()
    {
        var registry = new WhiteboardAccessRegistry();
        var changes = new List<(string SurfaceId, byte[] Png)>();
        registry.SnapshotChanged += (surfaceId, png) => changes.Add((surfaceId, png));
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("session-a", "surface-1");
        byte[] updated = [9, 9, 9];

        registry.UpdateSnapshot("surface-1", updated);

        Assert.Equal(updated, registry.ReadCoupled("session-a", "surface-1"));
        Assert.Equal(("surface-1", updated), Assert.Single(changes));
    }
}
