namespace Cockpit.Core.Abstractions.Whiteboard;

// An open whiteboard surface the agent could ask to read: its stable id and the name the operator sees.
public sealed record WhiteboardSurface(string SurfaceId, string Name);

// What one session holds on a surface. Unlike DiagramCoupling there is no capability enum — AC-823 offers exactly
// one capability (Read; there is no edit_whiteboard, the agent never writes to the canvas) — but coupling is still
// separate from granting it, so "coupled, nothing granted yet" stays a real, distinct state (AC-810's precedent).
public sealed record WhiteboardCoupling(string SessionId, bool CanRead);

// A surface as `list_whiteboards` reports it to one agent session — the surface plus what that session already
// holds on it, or null when there is no coupling at all yet.
public sealed record WhiteboardSurfaceView(string SurfaceId, string Name, WhiteboardCoupling? Coupling);

// Raised when a surface's coupling changes, so its UI can show, reword or hide its "agent connected" bar.
// `Coupling` is null when it just decoupled.
public sealed record WhiteboardCouplingChange(string SurfaceId, WhiteboardCoupling? Coupling);

/// <summary>
/// The source of truth for whiteboard-surface access (AC-823) — the whiteboard counterpart to
/// <c>IDiagramAccessRegistry</c> (AC-810); read that one first. Deviations: one capability only (Read — there is no
/// edit_whiteboard), and what a surface holds is a rendered PNG snapshot (AC-821's
/// <c>IWhiteboardSnapshotRenderer</c> output), not text.
/// </summary>
public interface IWhiteboardAccessRegistry
{
    // ---- Producer side (the whiteboard panel/UI layer) ----

    /// <summary>Records that a whiteboard surface is open, seeded with its current snapshot. Idempotent — re-registering updates the name but leaves the snapshot and any coupling alone.</summary>
    void SurfaceOpened(string surfaceId, string name, byte[] initialSnapshotPng);

    /// <summary>Records that a surface closed: any coupling on it is broken automatically.</summary>
    void SurfaceClosed(string surfaceId);

    /// <summary>The board changing on screen: keeps the registry's copy — what an agent reads — in step with what the operator sees. Raises <see cref="SnapshotChanged"/>.</summary>
    void UpdateSnapshot(string surfaceId, byte[] snapshotPng);

    /// <summary>Raised whenever a surface's snapshot changes, so anything watching it can re-render.</summary>
    event Action<string, byte[]>? SnapshotChanged;

    /// <summary>Raised on a coupling change (coupled or decoupled by close/session-end/operator Disconnect) so the surface can show or hide its "agent connected" bar.</summary>
    event Action<WhiteboardCouplingChange>? CouplingChanged;

    /// <summary>The operator's Disconnect on a surface: breaks the coupling at once.</summary>
    void Disconnect(string surfaceId);

    /// <summary>The surface's current snapshot regardless of coupling — what the operator already sees on their own screen. Host-trusted: used to build a truthful consent prompt, never handed to an agent without that agent separately holding read. Null for an unknown surface or one with no snapshot yet.</summary>
    byte[]? PeekSnapshot(string surfaceId);

    // ---- Consumer side (the cockpit-whiteboard MCP tools) ----

    /// <summary>The open surfaces as this agent session sees them, each with what this session already holds on it.</summary>
    IReadOnlyList<WhiteboardSurfaceView> ListSurfaces(string sessionId);

    /// <summary>Finds an open surface by its id or its operator-facing name, or null if there is no such surface.</summary>
    WhiteboardSurface? Resolve(string surfaceRef);

    /// <summary>What this session holds on the surface, or null when there is no coupling at all — so a caller can tell "never coupled" from "coupled, not granted yet".</summary>
    WhiteboardCoupling? CouplingOf(string sessionId, string surfaceId);

    /// <summary>Whether a <em>different</em> agent session holds any coupling on the surface — exclusivity: a second agent is refused.</summary>
    bool IsCoupledByAnother(string sessionId, string surfaceId);

    /// <summary>Establishes a zero-capability coupling if this session holds none yet on the surface. Idempotent. Throws for an unknown surface, or one coupled to a different session.</summary>
    void Couple(string sessionId, string surfaceId);

    /// <summary>Grants this session Read on the surface (creating a coupling first if needed). Throws for an unknown surface, or one coupled to a different session.</summary>
    void Grant(string sessionId, string surfaceId);

    /// <summary>The surface's current snapshot, or null when this session does not hold Read on it.</summary>
    byte[]? ReadCoupled(string sessionId, string surfaceId);

    /// <summary>Breaks every coupling this agent session held (its session ended or crashed).</summary>
    void SessionEnded(string sessionId);
}
