namespace Cockpit.Core.Abstractions.Diagrams;

// An open diagram surface the agent could ask to read or edit: its stable id and the name the operator sees.
public sealed record DiagramSurface(string SurfaceId, string Name);

// What an agent may do with a coupled surface. Unlike terminal's Watch/Drive, `Edit` is asked and granted
// separately from `Read` rather than a strict superset in the type itself — see DiagramCoupling below for why a
// coupling still needs to represent holding neither.
public enum DiagramCapability
{
    Read,
    Edit,
}

// What one session holds on a surface. Not "coupled or not": both flags false is a real state (AC-816's quick-start
// couples before either capability is asked for) — null means never coupled, this means coupled with nothing
// granted yet. Granting Edit always sets CanRead too: editing something you cannot see is not a narrower grant.
public sealed record DiagramCoupling(string SessionId, bool CanRead, bool CanEdit)
{
    public bool HasAnyCapability => CanRead || CanEdit;
}

// A surface as `list_diagrams` reports it to one agent session — the surface plus what that session already holds
// on it, or null when there is no coupling at all yet.
public sealed record DiagramSurfaceView(string SurfaceId, string Name, DiagramCoupling? Coupling);

// Raised when a surface's coupling changes, so its UI can show, reword or hide its "agent connected" bar.
// `Coupling` is null when it just decoupled.
public sealed record DiagramCouplingChange(string SurfaceId, DiagramCoupling? Coupling);

// An edit an agent delivered via `edit_diagram` (AC-825), awaiting the operator's per-block accept/reject before
// any of it reaches the surface's stored source — the diff-poort itself. `FidelityFindings` is AC-808's report on
// `ProposedText`, carried on the proposal so it is visible before acceptance, not only on the result afterwards.
public sealed record DiagramProposal(
    string SurfaceId,
    string SessionId,
    string ProposedText,
    string ChangeSummary,
    IReadOnlyList<string> FidelityFindings,
    IReadOnlyList<DiagramDiffBlock> Blocks);

/// <summary>
/// The source of truth for diagram-surface access (AC-810) — the diagram counterpart to
/// <c>ITerminalAccessRegistry</c> (AC-34); read that one first. Deviations: a diagram is a state, not a stream, so
/// <see cref="ReadCoupled"/> always returns it as it stands; and a coupling can exist with zero capabilities.
/// </summary>
public interface IDiagramAccessRegistry
{
    // ---- Producer side (the diagram panel/UI layer) ----

    /// <summary>Records that a diagram surface is open, seeded with its current Mermaid source. Idempotent — re-registering updates the name but leaves the text and any coupling alone.</summary>
    void SurfaceOpened(string surfaceId, string name, string initialText);

    /// <summary>Records that a surface closed: any coupling on it is broken automatically.</summary>
    void SurfaceClosed(string surfaceId);

    /// <summary>The operator editing the diagram directly (once a surface offers that): keeps the registry's copy — what an agent reads — in step with what is on screen. Raises <see cref="TextChanged"/>.</summary>
    void UpdateText(string surfaceId, string text);

    /// <summary>Raised whenever a surface's text changes, from the operator (<see cref="UpdateText"/>) or from a coupled agent (<see cref="WriteCoupled"/>), so its panel can re-render.</summary>
    event Action<string, string>? TextChanged;

    /// <summary>Raised on a coupling change (coupled, widened, or decoupled by close/session-end/operator Disconnect) so the surface can show or hide its "agent connected" bar.</summary>
    event Action<DiagramCouplingChange>? CouplingChanged;

    /// <summary>The operator's Disconnect on a surface: breaks the coupling at once, whatever capabilities it held.</summary>
    void Disconnect(string surfaceId);

    /// <summary>The surface's current text regardless of coupling — what the operator already sees on their own screen. Host-trusted: used to build a truthful consent prompt and to compute a fidelity report, never handed to an agent without that agent separately holding <see cref="DiagramCapability.Read"/>. Null for an unknown surface.</summary>
    string? PeekText(string surfaceId);

    // ---- Consumer side (the cockpit-diagram MCP tools) ----

    /// <summary>The open surfaces as this agent session sees them, each with what this session already holds on it.</summary>
    IReadOnlyList<DiagramSurfaceView> ListSurfaces(string sessionId);

    /// <summary>Finds an open surface by its id or its operator-facing name, or null if there is no such surface.</summary>
    DiagramSurface? Resolve(string surfaceRef);

    /// <summary>What this session holds on the surface, or null when there is no coupling at all — so a caller can tell "never coupled" from "coupled, nothing granted yet".</summary>
    DiagramCoupling? CouplingOf(string sessionId, string surfaceId);

    /// <summary>Whether a <em>different</em> agent session holds any coupling on the surface, zero-capability included — exclusivity: a second agent is refused.</summary>
    bool IsCoupledByAnother(string sessionId, string surfaceId);

    /// <summary>Establishes a zero-capability coupling if this session holds none yet on the surface. Idempotent. Throws for an unknown surface, or one coupled to a different session.</summary>
    void Couple(string sessionId, string surfaceId);

    /// <summary>Widens this session's coupling to also hold <paramref name="capability"/> (creating a zero-capability coupling first if needed). <see cref="DiagramCapability.Edit"/> grants <see cref="DiagramCapability.Read"/> alongside it. Throws for an unknown surface, or one coupled to a different session.</summary>
    void Grant(string sessionId, string surfaceId, DiagramCapability capability);

    /// <summary>The surface's current text, or null when this session does not hold <see cref="DiagramCapability.Read"/> on it.</summary>
    string? ReadCoupled(string sessionId, string surfaceId);

    /// <summary>Writes new text into the surface and raises <see cref="TextChanged"/>. Returns false when this session does not hold <see cref="DiagramCapability.Edit"/> on it.</summary>
    bool WriteCoupled(string sessionId, string surfaceId, string text);

    /// <summary>Breaks every coupling this agent session held (its session ended or crashed).</summary>
    void SessionEnded(string sessionId);

    // ---- The diff-poort (AC-825): a proposal sits between "delivered" and "applied" ----

    /// <summary>Raised when a surface's pending proposal changes — set on a fresh <see cref="Propose"/>, null once resolved, discarded, or the surface/session that made it goes away.</summary>
    event Action<string, DiagramProposal?>? ProposalChanged;

    /// <summary>Records `proposedText` as a pending proposal on `surfaceId`, computed against the surface's current text — it does not touch the stored source. Returns false when `sessionId` does not hold <see cref="DiagramCapability.Edit"/> on the surface.</summary>
    bool Propose(string sessionId, string surfaceId, string proposedText, string changeSummary, IReadOnlyList<string> fidelityFindings);

    /// <summary>The surface's pending proposal, or null when there is none.</summary>
    DiagramProposal? PendingProposal(string surfaceId);

    /// <summary>Applies the pending proposal's blocks using the operator's per-block decision (see <see cref="DiagramDiff.Apply"/>), writes the merged result into the surface (raising <see cref="TextChanged"/>), and clears the proposal. Returns false when there is no pending proposal on this surface.</summary>
    bool ResolveProposal(string surfaceId, IReadOnlySet<int> acceptedBlocks);

    /// <summary>Discards the surface's pending proposal without writing anything — the whole thing, or whatever of it was still undecided.</summary>
    bool DiscardProposal(string surfaceId);
}
