namespace Cockpit.Core.Abstractions.Whiteboard;

// An open whiteboard surface the agent could ask to read or put something on: its stable id and the name the
// operator sees.
public sealed record WhiteboardSurface(string SurfaceId, string Name);

// What an agent may do with a coupled surface. AC-820/AC-823 deliberately shipped Read alone ("an agent never
// writes to the canvas"); Raymond lifted that boundary on 2026-08-16 (AC-854) — a whiteboard is a collab surface,
// so the agent works on it too. Write is asked and granted separately from Read, like DiagramCapability.Edit.
public enum WhiteboardCapability
{
    Read,
    Write,
}

// What one session holds on a surface: "coupled, nothing granted yet" is a real state (AC-810's precedent), and
// granting Write always sets CanRead too — putting something on a board you cannot see is not a narrower grant.
// LastReadAt is AC-842's "gelezen 15:11": when this session's read_whiteboard last returned a snapshot.
public sealed record WhiteboardCoupling(string SessionId, bool CanRead, bool CanWrite = false, DateTimeOffset? LastReadAt = null);

// One object an agent asks to put on a board (AC-854): a template shape, a sticky note or a bare label, in the
// board's own coordinates. Shape names are PlacedShapeKind's, matched case-insensitively by the whiteboard plugin.
public sealed record WhiteboardPlacement(string Shape, string? Text, double X, double Y, double Width, double Height);

// A surface as `list_whiteboards` reports it to one agent session — the surface plus what that session already
// holds on it, or null when there is no coupling at all yet.
public sealed record WhiteboardSurfaceView(string SurfaceId, string Name, WhiteboardCoupling? Coupling);

// Raised when a surface's coupling changes, so its UI can show, reword or hide its "agent connected" bar.
// `Coupling` is null when it just decoupled.
public sealed record WhiteboardCouplingChange(string SurfaceId, WhiteboardCoupling? Coupling);

// AC-853: one journaled agent action on a surface's objects. Only `Place` can be undone today — the board has no
// operator hand-edit path and no fine-grained edit tools yet (AC-852's diagram counterpart), so an `Erase` is
// journaled for the strip to show but its own object cannot be losslessly restored; see Revert.
public enum WhiteboardHistoryKind
{
    Place,
    Erase,
}

public sealed record WhiteboardHistoryEntry(string Id, string Origin, WhiteboardHistoryKind Kind, string ObjectId, string Summary, DateTime When, bool Reverted);

/// <summary>
/// The source of truth for whiteboard-surface access (AC-823) — the counterpart to <c>IDiagramAccessRegistry</c>
/// (AC-810); read that one first. Deviations: a surface holds a rendered PNG snapshot, not text, and writes add
/// objects one at a time (AC-854) rather than replace the board, so an agent never overwrites the operator's work.
/// </summary>
public interface IWhiteboardAccessRegistry
{
    // ---- Producer side (the whiteboard panel/UI layer) ----

    /// <summary>
    /// Records that a whiteboard surface is open, seeded with its current snapshot. Idempotent — re-registering updates the name but leaves the snapshot and any coupling alone.
    /// </summary>
    void SurfaceOpened(string surfaceId, string name, byte[] initialSnapshotPng);

    /// <summary>
    /// Records that a surface closed: any coupling on it is broken automatically.
    /// </summary>
    void SurfaceClosed(string surfaceId);

    /// <summary>
    /// The board changing on screen: keeps the registry's copy — what an agent reads — in step with what the operator sees. Raises <see cref="SnapshotChanged"/>.
    /// </summary>
    void UpdateSnapshot(string surfaceId, byte[] snapshotPng);

    /// <summary>
    /// Raised whenever a surface's snapshot changes, so anything watching it can re-render.
    /// </summary>
    event Action<string, byte[]>? SnapshotChanged;

    /// <summary>
    /// Raised on a coupling change (coupled or decoupled by close/session-end/operator Disconnect) so the surface can show or hide its "agent connected" bar.
    /// </summary>
    event Action<WhiteboardCouplingChange>? CouplingChanged;

    /// <summary>
    /// The operator's Disconnect on a surface: breaks the coupling at once.
    /// </summary>
    void Disconnect(string surfaceId);

    /// <summary>
    /// The surface's current snapshot regardless of coupling — what the operator already sees on their own screen. Host-trusted: used to build a truthful consent prompt, never handed to an agent without that agent separately holding read. Null for an unknown surface or one with no snapshot yet.
    /// </summary>
    byte[]? PeekSnapshot(string surfaceId);

    // ---- Consumer side (the cockpit-whiteboard MCP tools) ----

    /// <summary>
    /// The open surfaces as this agent session sees them, each with what this session already holds on it.
    /// </summary>
    IReadOnlyList<WhiteboardSurfaceView> ListSurfaces(string sessionId);

    /// <summary>
    /// Finds an open surface by its id or its operator-facing name, or null if there is no such surface.
    /// </summary>
    WhiteboardSurface? Resolve(string surfaceRef);

    /// <summary>
    /// What this session holds on the surface, or null when there is no coupling at all — so a caller can tell "never coupled" from "coupled, not granted yet".
    /// </summary>
    WhiteboardCoupling? CouplingOf(string sessionId, string surfaceId);

    /// <summary>
    /// Whether a <em>different</em> agent session holds any coupling on the surface — exclusivity: a second agent is refused.
    /// </summary>
    bool IsCoupledByAnother(string sessionId, string surfaceId);

    /// <summary>
    /// Establishes a zero-capability coupling if this session holds none yet on the surface. Idempotent. Throws for an unknown surface, or one coupled to a different session.
    /// </summary>
    void Couple(string sessionId, string surfaceId);

    /// <summary>
    /// Widens this session's coupling to also hold <paramref name="capability"/> (creating a coupling first if needed). <see cref="WhiteboardCapability.Write"/> grants <see cref="WhiteboardCapability.Read"/> alongside it. Throws for an unknown surface, or one coupled to a different session.
    /// </summary>
    void Grant(string sessionId, string surfaceId, WhiteboardCapability capability = WhiteboardCapability.Read);

    /// <summary>
    /// The surface's current snapshot, or null when this session does not hold Read on it.
    /// </summary>
    byte[]? ReadCoupled(string sessionId, string surfaceId);

    /// <summary>
    /// Puts one object on the surface on this session's behalf and raises <see cref="ObjectPlaced"/>. Returns the id the object got, or null when this session does not hold <see cref="WhiteboardCapability.Write"/>.
    /// </summary>
    string? PlaceCoupled(string sessionId, string surfaceId, WhiteboardPlacement placement);

    /// <summary>
    /// Removes an object this same session placed and raises <see cref="ObjectErased"/>. False for anything else — the operator's own work is not an agent's to take away (AC-854).
    /// </summary>
    bool ErasePlaced(string sessionId, string surfaceId, string objectId);

    /// <summary>
    /// Raised for each object an agent put on a surface, with the id stamped on it, so the board can draw it.
    /// </summary>
    event Action<string, string, WhiteboardPlacement>? ObjectPlaced;

    /// <summary>
    /// Raised when an agent takes back an object it placed itself — surface id and object id.
    /// </summary>
    event Action<string, string>? ObjectErased;

    /// <summary>
    /// Records that this session's read_whiteboard just returned a snapshot, so the board can show when it was last read (AC-842). No-op when this session does not hold Read.
    /// </summary>
    void MarkRead(string sessionId, string surfaceId);

    /// <summary>
    /// Breaks every coupling this agent session held (its session ended or crashed).
    /// </summary>
    void SessionEnded(string sessionId);

    // ---- Undo (AC-853) ----

    /// <summary>
    /// This surface's journaled agent actions, oldest first — what the activity strip (AC-848) shows per line, with a targeted revert offered on the ones that support it.
    /// </summary>
    IReadOnlyList<WhiteboardHistoryEntry> History(string surfaceId);

    /// <summary>
    /// Raised whenever a surface's history changes: a new action journaled, or one marked reverted.
    /// </summary>
    event Action<string>? HistoryChanged;

    /// <summary>
    /// Undoes the one journaled <see cref="WhiteboardHistoryKind.Place"/> named by <paramref name="entryId"/> — takes
    /// that object back regardless of who is coupled now, unlike <see cref="ErasePlaced"/>. Null when it landed, else
    /// the refusal reason: unknown entry, already reverted, gone already, or an <see cref="WhiteboardHistoryKind.Erase"/> (AC-853).
    /// </summary>
    string? Revert(string surfaceId, string entryId);
}
