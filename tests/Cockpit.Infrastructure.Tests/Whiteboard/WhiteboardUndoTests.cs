using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Infrastructure.Whiteboard;

namespace Cockpit.Infrastructure.Tests.Whiteboard;

/// <summary>
/// AC-853's whiteboard slice: narrower than the diagram's by design (documented in the PR, not a silent gap) — the
/// board only has agent-authored, add-only structured actions (AC-854) to journal at all, so revert only ever
/// undoes a Place, taking that object back regardless of whether the session that placed it is still coupled.
/// </summary>
public class WhiteboardUndoTests
{
    private static readonly byte[] Png = [1, 2, 3];

    [Fact]
    public void Revert_APlace_RemovesTheObject_EvenAfterTheAgentDisconnected()
    {
        // ErasePlaced needs the placing session to still hold Write; the operator's revert from the strip must not.
        var registry = new WhiteboardAccessRegistry();
        var erased = new List<(string SurfaceId, string ObjectId)>();
        registry.ObjectErased += (surfaceId, objectId) => erased.Add((surfaceId, objectId));
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("agent-1", "surface-1", WhiteboardCapability.Write);
        var objectId = registry.PlaceCoupled("agent-1", "surface-1", new WhiteboardPlacement("stickynote", "Idee", 10, 20, 140, 140))!;
        var entry = Assert.Single(registry.History("surface-1"));

        registry.SessionEnded("agent-1");
        var refusal = registry.Revert("surface-1", entry.Id);

        Assert.Null(refusal);
        Assert.Equal(("surface-1", objectId), Assert.Single(erased));
    }

    [Fact]
    public void Revert_IsMarkedOnTheEntry_NotErasedFromHistory()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("agent-1", "surface-1", WhiteboardCapability.Write);
        registry.PlaceCoupled("agent-1", "surface-1", new WhiteboardPlacement("stickynote", "Idee", 10, 20, 140, 140));
        var entry = Assert.Single(registry.History("surface-1"));

        registry.Revert("surface-1", entry.Id);

        var after = Assert.Single(registry.History("surface-1"));
        Assert.Equal(entry.Id, after.Id);
        Assert.True(after.Reverted);
    }

    [Fact]
    public void Revert_AnEraseEntry_IsRefused_TakingBackARemovedObjectIsNotSupportedYet()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("agent-1", "surface-1", WhiteboardCapability.Write);
        var objectId = registry.PlaceCoupled("agent-1", "surface-1", new WhiteboardPlacement("stickynote", "Idee", 10, 20, 140, 140))!;
        registry.ErasePlaced("agent-1", "surface-1", objectId);
        var eraseEntry = registry.History("surface-1").Single(candidate => candidate.Kind == WhiteboardHistoryKind.Erase);

        var refusal = registry.Revert("surface-1", eraseEntry.Id);

        Assert.NotNull(refusal);
    }

    [Fact]
    public void Revert_TheSameEntryTwice_IsRefusedTheSecondTime()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("surface-1", "Sprint planning", Png);
        registry.Grant("agent-1", "surface-1", WhiteboardCapability.Write);
        registry.PlaceCoupled("agent-1", "surface-1", new WhiteboardPlacement("stickynote", "Idee", 10, 20, 140, 140));
        var entry = Assert.Single(registry.History("surface-1"));
        Assert.Null(registry.Revert("surface-1", entry.Id));

        var refusal = registry.Revert("surface-1", entry.Id);

        Assert.NotNull(refusal);
    }
}
