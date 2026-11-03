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

}
