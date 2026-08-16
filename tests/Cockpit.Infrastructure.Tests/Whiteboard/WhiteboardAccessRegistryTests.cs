using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Infrastructure.Whiteboard;

namespace Cockpit.Infrastructure.Tests.Whiteboard;

/// <summary>
/// The coupling rules behind the whiteboard-access MCP (AC-823): two capabilities asked and granted separately
/// (Read, and Write since AC-854 lifted AC-820's "an agent never writes to the canvas"), a coupling can exist with
/// neither granted, one agent per surface, and a surface close or a session end decouples on its own. The write path
/// only adds: an agent takes back its own objects and nothing else. Mirrors DiagramAccessRegistryTests (AC-810).
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
    public void PlaceCoupled_NeedsWrite_WhichReadAloneDoesNotGive()
    {
        var registry = new WhiteboardAccessRegistry();
        var placed = new List<(string SurfaceId, string ObjectId, WhiteboardPlacement Placement)>();
        registry.ObjectPlaced += (surfaceId, objectId, placement) => placed.Add((surfaceId, objectId, placement));
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        var placement = new WhiteboardPlacement("stickynote", "Idee", 10, 20, 140, 140);

        registry.Grant("session-a", "surface-1", WhiteboardCapability.Read);
        Assert.Null(registry.PlaceCoupled("session-a", "surface-1", placement));
        Assert.Empty(placed);

        registry.Grant("session-a", "surface-1", WhiteboardCapability.Write);
        var objectId = registry.PlaceCoupled("session-a", "surface-1", placement);

        Assert.NotNull(objectId);
        Assert.Equal(("surface-1", objectId!, placement), Assert.Single(placed));
    }

    [Fact]
    public void Grant_Write_GrantsReadAlongsideIt()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);

        registry.Grant("session-a", "surface-1", WhiteboardCapability.Write);

        var coupling = registry.CouplingOf("session-a", "surface-1");
        Assert.True(coupling!.CanRead);
        Assert.True(coupling.CanWrite);
        Assert.Equal(Png, registry.ReadCoupled("session-a", "surface-1"));
    }

    [Fact]
    public void Grant_Read_DoesNotNarrowAWriteGrantAlreadyHeld()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("session-a", "surface-1", WhiteboardCapability.Write);

        registry.Grant("session-a", "surface-1", WhiteboardCapability.Read);

        Assert.True(registry.CouplingOf("session-a", "surface-1")!.CanWrite);
    }

    [Fact]
    public void ErasePlaced_ReachesTheAgentsOwnObjectsOnly_NeverTheOperatorsWork()
    {
        // AC-854's boundary that stays: the agent adds, and can take its own additions back — anything else on the
        // board (drawn or placed by the operator, so unknown to the registry) is refused, not removed.
        var registry = new WhiteboardAccessRegistry();
        var erased = new List<string>();
        registry.ObjectErased += (_, objectId) => erased.Add(objectId);
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("session-a", "surface-1", WhiteboardCapability.Write);
        var objectId = registry.PlaceCoupled("session-a", "surface-1", new WhiteboardPlacement("rectangle", null, 0, 0, 120, 80))!;

        Assert.False(registry.ErasePlaced("session-a", "surface-1", "an-object-the-operator-drew"));
        Assert.Empty(erased);

        Assert.True(registry.ErasePlaced("session-a", "surface-1", objectId));
        Assert.Equal(objectId, Assert.Single(erased));
        Assert.False(registry.ErasePlaced("session-a", "surface-1", objectId));
    }

    [Fact]
    public void ErasePlaced_DoesNotReachAnotherSessionsObject()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("session-a", "surface-1", WhiteboardCapability.Write);
        var objectId = registry.PlaceCoupled("session-a", "surface-1", new WhiteboardPlacement("rectangle", null, 0, 0, 120, 80))!;

        registry.Disconnect("surface-1");
        registry.Grant("session-b", "surface-1", WhiteboardCapability.Write);

        Assert.False(registry.ErasePlaced("session-b", "surface-1", objectId));
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
    public void MarkRead_OnlyStampsACouplingThatHoldsRead_AndAnnounces()
    {
        var registry = new WhiteboardAccessRegistry();
        var changes = new List<WhiteboardCouplingChange>();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Couple("session-a", "surface-1");

        registry.MarkRead("session-a", "surface-1");
        Assert.Null(registry.CouplingOf("session-a", "surface-1")!.LastReadAt);

        registry.Grant("session-a", "surface-1");
        registry.CouplingChanged += changes.Add;
        registry.MarkRead("session-a", "surface-1");

        Assert.NotNull(registry.CouplingOf("session-a", "surface-1")!.LastReadAt);
        Assert.Single(changes);
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
