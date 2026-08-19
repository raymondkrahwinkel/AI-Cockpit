namespace Cockpit.Core.Abstractions.Diagrams;

// What the render engine dropped on the floor (AC-808). Mermaider can leave a construct out of its SVG
// without throwing, warning, or leaving a gap — the picture looks complete and says something other than
// the source does, which is worse than a visible failure because a decision gets taken on it. Every finding
// is a finished sentence, so both consumers of a render — the operator's surface and the agent's MCP reply
// — say the same thing without each inventing its own phrasing.
public sealed record DiagramFidelity(IReadOnlyList<string> Findings)
{
    public bool IsComplete => Findings.Count == 0;
}

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
// any of it reaches the surface's stored source — the diff gate itself. `FidelityFindings` is AC-808's report on
// `ProposedText`, carried on the proposal so it is visible before acceptance, not only on the result afterwards.
public sealed record DiagramProposal(
    string SurfaceId,
    string SessionId,
    string ProposedText,
    string ChangeSummary,
    IReadOnlyList<string> FidelityFindings,
    IReadOnlyList<DiagramDiffBlock> Blocks);

// The changes a hand-edit on the diagram surface can make (AC-841) — the same set the agent's per-object tools
// make, so both sides reach the source through one path. Which of them a surface accepts depends on its diagram
// type (AC-899): the first seven are flowchart/graph, the rest erDiagram.
public enum DiagramHandEditKind
{
    AddNode,
    RenameNode,
    RemoveNode,
    Connect,
    Disconnect,
    RelabelConnection, // AC-909: the label on an existing connection, without touching either end.
    SetNodeShape, // AC-909: the delimiters a node is drawn with, without touching its label.
    AddEntity,
    RenameEntity,
    RemoveEntity,
    SetAttribute,
    RemoveAttribute,
    Relate,
    Unrelate,
}

// How many of one entity a relationship end stands for (AC-899). Written as Mermaid's own crow's-foot pairs —
// `||`/`|o`/`}|`/`}o` on the left, `||`/`o|`/`|{`/`o{` on the right.
public enum DiagramErCardinality
{
    One,
    ZeroOrOne,
    OneOrMore,
    ZeroOrMore,
}

// The five node shapes the flowchart grammar can express one at a time (AC-909) — named for the picker, not by
// Mermaid's own bracket syntax (that mapping lives in FlowchartObjectEdit).
public enum DiagramNodeShape
{
    Rectangle,
    Rounded,
    Diamond,
    Stadium,
    Subroutine,
}

// One hand-edit. `Id` is the node or entity, or the connection's tail when `To` is set; the ER kinds carry their
// own fields rather than reading a meaning into `To`/`Label` that the flowchart kinds do not have (AC-899).
public sealed record DiagramHandEdit(DiagramHandEditKind Kind, string Id, string? To = null, string? Label = null)
{
    public string? Attribute { get; init; }

    public string? AttributeType { get; init; }

    // "PK", "FK" or "UK" as Mermaid writes them, or null for an attribute that carries no key marker.
    public string? AttributeKey { get; init; }

    public DiagramErCardinality? FromCardinality { get; init; }

    public DiagramErCardinality? ToCardinality { get; init; }

    // Only meaningful for SetNodeShape (AC-909).
    public DiagramNodeShape? Shape { get; init; }
}

// The diagram types whose objects can be edited one at a time (AC-899). Anything else renders and can be replaced
// wholesale, but has no per-object grammar.
public enum DiagramEditDialect
{
    Flowchart,
    Er,
    Unsupported,
}

// What hand-editing a surface offers right now: which button set belongs on it, and — when it offers none — the
// operator-facing reason those buttons are off rather than enabled-and-then-refusing.
public sealed record DiagramEditSupport(DiagramEditDialect Dialect, string? Reason);

// One attribute inside an erDiagram entity block, as the entity's own flyout lists it (AC-899). `Key` is "PK",
// "FK" or "UK", or null when the attribute carries no key marker.
public sealed record DiagramErAttribute(string Type, string Name, string? Key);

// An agent asking for a diagram it wrote to be put on screen so the operator can go through it (AC-835). The
// registry is the only seam between core and the plugin, so the request travels over it: core mints the ids and
// asks consent, the plugin opens the window. `SessionId` is the caller the surface couples to on arrival.
public sealed record DiagramOpenRequest(string SurfaceId, string Name, string Text, string SessionId);

// AC-853: one journaled per-object edit, operator or agent, with enough to compute its own inverse against the
// surface as it stands *now*. `ObjectKey` is the node id, or "from->to" for a connection (the strip's jump-to convention).
public sealed record DiagramHistoryEntry(string Id, string Origin, DiagramHandEditKind Kind, string ObjectKey, string Summary, DateTime When, bool Reverted);

// AC-849: a question the operator planted on one object, landed as a "📍 pin N" reference in the coupled session
// over ICockpitHost.SendToSessionAsync. `ObjectKey` is the same HoldKey a history entry uses, so a pin keeps
// pointing at its object across a Mermaid relayout the way the operator's hold and the activity strip already do.
public sealed record DiagramPin(string Id, string ObjectKey, string Question, DateTime When, bool Closed);

/// <summary>
/// The source of truth for diagram-surface access (AC-810) — the diagram counterpart to
/// <c>ITerminalAccessRegistry</c> (AC-34); read that one first. Deviations: a diagram is a state, not a stream, so
/// <see cref="ReadCoupled"/> always returns it as it stands; and a coupling can exist with zero capabilities.
/// </summary>
public interface IDiagramAccessRegistry
{
    // ---- Producer side (the diagram panel/UI layer) ----

    /// <summary>
    /// Records that a diagram surface is open, seeded with its current Mermaid source. Idempotent — re-registering updates the name but leaves the text and any coupling alone.
    /// </summary>
    void SurfaceOpened(string surfaceId, string name, string initialText);

    /// <summary>
    /// Records that a surface closed: any coupling on it is broken automatically.
    /// </summary>
    void SurfaceClosed(string surfaceId);

    /// <summary>
    /// The operator editing the diagram directly (once a surface offers that): keeps the registry's copy — what an agent reads — in step with what is on screen. Raises <see cref="TextChanged"/>.
    /// </summary>
    void UpdateText(string surfaceId, string text);

    /// <summary>
    /// Raised whenever a surface's text changes, from the operator (<see cref="UpdateText"/>) or from a coupled agent (<see cref="WriteCoupled"/>), so its panel can re-render.
    /// </summary>
    event Action<string, string>? TextChanged;

    /// <summary>
    /// Raised on a coupling change (coupled, widened, or decoupled by close/session-end/operator Disconnect) so the surface can show or hide its "agent connected" bar.
    /// </summary>
    event Action<DiagramCouplingChange>? CouplingChanged;

    /// <summary>
    /// The operator's Disconnect on a surface: breaks the coupling at once, whatever capabilities it held.
    /// </summary>
    void Disconnect(string surfaceId);

    /// <summary>
    /// The surface's current text regardless of coupling — what the operator already sees on their own screen. Host-trusted: used to build a truthful consent prompt and to compute a fidelity report, never handed to an agent without that agent separately holding <see cref="DiagramCapability.Read"/>. Null for an unknown surface.
    /// </summary>
    string? PeekText(string surfaceId);

    /// <summary>
    /// What the render engine would drop from <paramref name="source"/> (AC-808), or null when it cannot draw that
    /// source at all — the one place either side asks that question, so the tools need no render engine of their
    /// own. Takes the source rather than a surface id: <c>open_diagram</c> checks text that is not on any surface yet.
    /// </summary>
    DiagramFidelity? CheckFidelity(string source);

    // ---- Consumer side (the cockpit-diagram MCP tools) ----

    /// <summary>
    /// The open surfaces as this agent session sees them, each with what this session already holds on it.
    /// </summary>
    IReadOnlyList<DiagramSurfaceView> ListSurfaces(string sessionId);

    /// <summary>
    /// Finds an open surface by its id or its operator-facing name, or null if there is no such surface.
    /// </summary>
    DiagramSurface? Resolve(string surfaceRef);

    /// <summary>
    /// What this session holds on the surface, or null when there is no coupling at all — so a caller can tell "never coupled" from "coupled, nothing granted yet".
    /// </summary>
    DiagramCoupling? CouplingOf(string sessionId, string surfaceId);

    /// <summary>
    /// Whether a <em>different</em> agent session holds any coupling on the surface, zero-capability included — exclusivity: a second agent is refused.
    /// </summary>
    bool IsCoupledByAnother(string sessionId, string surfaceId);

    /// <summary>
    /// Establishes a zero-capability coupling if this session holds none yet on the surface. Idempotent. Throws for an unknown surface, or one coupled to a different session.
    /// </summary>
    void Couple(string sessionId, string surfaceId);

    /// <summary>
    /// Widens this session's coupling to also hold <paramref name="capability"/> (creating a zero-capability coupling first if needed). <see cref="DiagramCapability.Edit"/> grants <see cref="DiagramCapability.Read"/> alongside it. Throws for an unknown surface, or one coupled to a different session.
    /// </summary>
    void Grant(string sessionId, string surfaceId, DiagramCapability capability);

    /// <summary>
    /// The surface's current text, or null when this session does not hold <see cref="DiagramCapability.Read"/> on it.
    /// </summary>
    string? ReadCoupled(string sessionId, string surfaceId);

    /// <summary>
    /// Writes new text into the surface and raises <see cref="TextChanged"/>. Returns false when this session does not hold <see cref="DiagramCapability.Edit"/> on it.
    /// </summary>
    bool WriteCoupled(string sessionId, string surfaceId, string text);

    /// <summary>
    /// Applies a per-object edit (AC-852) under the registry's own lock: <paramref name="edit"/> gets the text as it
    /// stands then and returns the new text plus a readable summary, or a null text to change nothing — so two edits
    /// naming different objects both land, neither overwriting the whole of the other, and <paramref name="kind"/>/
    /// <paramref name="objectKey"/> journal it for a later targeted <see cref="Revert"/> (AC-853).
    /// Raises <see cref="TextChanged"/>, <see cref="ObjectEdited"/> and <see cref="HistoryChanged"/>; false without
    /// <see cref="DiagramCapability.Edit"/> or when nothing changed.
    /// </summary>
    bool EditCoupled(string sessionId, string surfaceId, DiagramHandEditKind kind, string objectKey, Func<string, (string? Text, string Summary)> edit);

    /// <summary>
    /// Raised for each applied per-object edit with a one-line summary of what changed — what the activity strip (AC-848) shows per handling, rather than "the whole source was replaced".
    /// </summary>
    event Action<string, string>? ObjectEdited;

    /// <summary>
    /// Applies one hand-edit the operator made on the surface itself (AC-841), under the same lock as
    /// <see cref="EditCoupled"/>: one change, never a series of half states, and never overwriting an agent edit to a
    /// different object. Returns null when it landed, or the reason it was refused — an unknown surface, a change the
    /// per-object grammar cannot make, or one that would not leave valid Mermaid behind.
    /// </summary>
    string? ApplyHandEdit(string surfaceId, DiagramHandEdit edit);

    /// <summary>
    /// Which per-object grammar this surface's diagram type has, so its panel can offer that dialect's controls and
    /// disable them with a reason where there is none (AC-899). <see cref="DiagramEditDialect.Unsupported"/> for an unknown surface.
    /// </summary>
    DiagramEditSupport EditSupport(string surfaceId);

    /// <summary>
    /// The attributes an erDiagram entity carries right now, in source order — what the entity's own flyout lists,
    /// so the panel never has to read Mermaid itself. Empty for an unknown surface, entity, or diagram type.
    /// </summary>
    IReadOnlyList<DiagramErAttribute> EntityAttributes(string surfaceId, string entity);

    // ---- Undo (AC-853): the safety net that replaces the diff gate for the tools that write straight through ----

    /// <summary>
    /// This surface's journaled per-object edits, oldest first — both origins, so the activity strip (AC-848) can offer a targeted revert per line.
    /// </summary>
    IReadOnlyList<DiagramHistoryEntry> History(string surfaceId);

    /// <summary>
    /// Raised whenever a surface's history changes: a new edit journaled, or one marked reverted.
    /// </summary>
    event Action<string>? HistoryChanged;

    /// <summary>
    /// Undoes exactly the one journaled edit named by <paramref name="entryId"/> — never "the last change" — by
    /// applying its own inverse, object-scoped, to the surface as it stands right now, so an older entry can be
    /// reverted without touching a different object's edit made since. Null when it landed, else the refusal reason.
    /// </summary>
    string? Revert(string surfaceId, string entryId);

    // ---- The operator's "jij bewerkt" hold (AC-841/D-5) ----

    /// <summary>
    /// Marks an object on the surface as the operator's while they are editing it: a node by its id, a connection as "from-&gt;to". Idempotent.
    /// </summary>
    void HoldObject(string surfaceId, string objectId);

    /// <summary>
    /// Releases the operator's hold on an object.
    /// </summary>
    void ReleaseObject(string surfaceId, string objectId);

    /// <summary>
    /// Whether the operator is holding that object right now — an agent edit naming it is refused with a reason rather than applied or silently dropped.
    /// </summary>
    bool IsHeldByOperator(string surfaceId, string objectId);

    /// <summary>
    /// Breaks every coupling this agent session held (its session ended or crashed).
    /// </summary>
    void SessionEnded(string sessionId);

    // ---- An agent asking for a window (AC-835) ----

    /// <summary>
    /// Raised when an agent asked for a diagram to be opened, after the operator approved it — whoever draws diagram windows listens here.
    /// </summary>
    event Action<DiagramOpenRequest>? OpenRequested;

    /// <summary>
    /// Announces <paramref name="request"/> and remembers its caller, so the surface is coupled to it the moment the window registers it. False when nothing is listening at all — there is no diagram surface in this cockpit to open one on.
    /// </summary>
    bool RequestOpen(DiagramOpenRequest request);

    // ---- The diff gate (AC-825): a proposal sits between "delivered" and "applied" ----

    /// <summary>
    /// Raised when a surface's pending proposal changes — set on a fresh <see cref="Propose"/>, re-raised with recomputed blocks when the surface's text moved under it, null once resolved, discarded, or the surface/session that made it goes away.
    /// </summary>
    event Action<string, DiagramProposal?>? ProposalChanged;

    /// <summary>
    /// Records `proposedText` as a pending proposal on `surfaceId`, computed against the surface's current text — it does not touch the stored source. Returns false when `sessionId` does not hold <see cref="DiagramCapability.Edit"/> on the surface.
    /// </summary>
    bool Propose(string sessionId, string surfaceId, string proposedText, string changeSummary, IReadOnlyList<string> fidelityFindings);

    /// <summary>
    /// The surface's pending proposal, or null when there is none.
    /// </summary>
    DiagramProposal? PendingProposal(string surfaceId);

    /// <summary>
    /// Applies the pending proposal's blocks using the operator's per-block decision (see <see cref="DiagramDiff.Apply"/>), writes the merged result into the surface (raising <see cref="TextChanged"/>), and clears the proposal. The blocks it applies are always against the surface as it stands — a hand-edit or per-object edit under a waiting proposal rebases it (AC-845) rather than being overwritten by it. Returns false when there is no pending proposal on this surface.
    /// </summary>
    bool ResolveProposal(string surfaceId, IReadOnlySet<int> acceptedBlocks);

    /// <summary>
    /// Discards the surface's pending proposal without writing anything — the whole thing, or whatever of it was still undecided.
    /// </summary>
    bool DiscardProposal(string surfaceId);

    // ---- Pins (AC-849): the operator's question about one object, landed as a reference in the coupled session ----

    /// <summary>
    /// This surface's pins, oldest first — a pin's 1-based position in this list is the "N" in its "📍 pin N" reference.
    /// </summary>
    IReadOnlyList<DiagramPin> Pins(string surfaceId);

    /// <summary>
    /// Raised whenever a surface's pins change: a new one planted, or one closed.
    /// </summary>
    event Action<string>? PinsChanged;

    /// <summary>
    /// Plants a pin on `objectKey` and returns its id, so the caller can compose and send the "📍 pin N" reference itself.
    /// </summary>
    string AddPin(string surfaceId, string objectKey, string question);

    /// <summary>
    /// Marks a pin closed — the operator's own call that it has been answered, not a system-detected one. Idempotent.
    /// </summary>
    void ClosePin(string surfaceId, string pinId);
}
