namespace Cockpit.Core.Abstractions.Wireframe;

// An open wireframe surface the agent could ask to read or edit: its stable id and the name the operator sees.
public sealed record WireframeSurface(string SurfaceId, string Name);

// What an agent may do with a coupled surface. Read and Edit are asked and granted separately, as
// DiagramCapability's are; granting Edit always grants Read alongside it, never the other way round.
public enum WireframeCapability
{
    Read,
    Edit,
}

// What one session holds on a surface. Both flags false is a real state — coupled with nothing granted yet — where
// null means never coupled at all (AC-810's precedent). LastReadAt is when this session's read_wireframe last
// returned the source, the same "gelezen 15:11" the whiteboard shows (AC-842).
public sealed record WireframeCoupling(string SessionId, bool CanRead, bool CanEdit = false, DateTimeOffset? LastReadAt = null)
{
    public bool HasAnyCapability => CanRead || CanEdit;
}

// A surface as `list_wireframes` reports it to one agent session — the surface plus what that session already holds
// on it, or null when there is no coupling at all yet.
public sealed record WireframeSurfaceView(string SurfaceId, string Name, WireframeCoupling? Coupling);

// Raised when a surface's coupling changes, so its UI can show, reword or hide its "agent connected" bar.
// `Coupling` is null when it just decoupled.
public sealed record WireframeCouplingChange(string SurfaceId, WireframeCoupling? Coupling);

// An agent asking for a wireframe it wrote to be put on screen so the operator can go through it (AC-835). The
// registry is the only seam between core and the plugin, so the request travels over it: core mints the ids and
// asks consent, the plugin opens the window. `SessionId` is the caller the surface couples to on arrival.
public sealed record WireframeOpenRequest(string SurfaceId, string Name, string Text, string SessionId);

// The changes a wireframe surface records in its journal (AC-853). `Replace` is what WriteCoupled writes — the
// whole source at once — and is the one kind no WireframeComponentEdit produces.
public enum WireframeEditKind
{
    Replace,
    Add,
    SetText,
    Remove,
    Move,
}

// One per-component edit (AC-852's shape), addressed by line number: a wireframe line carries no id of its own, so
// the line the operator reads in the source box is what an agent names, what the journal records, and what the
// "jij bewerkt" hold is keyed on. Use the factories — each kind reads only the fields it needs.
public sealed record WireframeComponentEdit(
    WireframeEditKind Kind,
    int Component = 0,
    int Parent = 0,
    int? Position = null,
    string? Type = null,
    string? Text = null,
    string? Modifiers = null)
{
    public static WireframeComponentEdit Add(int parent, string type, string? text, string? modifiers, int? position) =>
        new(WireframeEditKind.Add, Parent: parent, Position: position, Type: type, Text: text, Modifiers: modifiers);

    public static WireframeComponentEdit SetText(int component, string text) =>
        new(WireframeEditKind.SetText, component, Text: text);

    public static WireframeComponentEdit Remove(int component) =>
        new(WireframeEditKind.Remove, component);

    public static WireframeComponentEdit Move(int component, int parent, int? position) =>
        new(WireframeEditKind.Move, component, parent, position);
}

// What an edit did: a one-line summary for the activity strip (AC-848), or the reason it was refused. `Summary` is
// empty exactly when `Refusal` is not.
public sealed record WireframeEditResult(string Summary, string? Refusal)
{
    public static WireframeEditResult Applied(string summary) => new(summary, null);

    public static WireframeEditResult Refused(string reason) => new("", reason);
}

// AC-853: one journaled edit, operator or agent. `ComponentKey` is the line the edit was aimed at, as it stood
// then — enough for the strip to jump to it, while the revert itself finds its lines back by content.
public sealed record WireframeHistoryEntry(string Id, string Origin, WireframeEditKind Kind, string ComponentKey, string Summary, DateTime When, bool Reverted);

/// <summary>
/// The source of truth for wireframe-surface access (AC-872) — the third registry beside
/// <c>IDiagramAccessRegistry</c> (AC-810) and <c>IWhiteboardAccessRegistry</c> (AC-823); read the diagram one first.
/// Deviations: a component is named by its line number rather than an id, and there is no diff gate — the journal
/// plus a targeted <see cref="Revert"/> is the whole safety net.
/// </summary>
public interface IWireframeAccessRegistry
{
    // ---- Producer side (the wireframe panel/UI layer) ----

    /// <summary>
    /// Records that a wireframe surface is open, seeded with its current source. Idempotent — re-registering
    /// updates the name but leaves the text and any coupling alone.
    /// </summary>
    void SurfaceOpened(string surfaceId, string name, string initialText);

    /// <summary>
    /// Records that a surface closed: any coupling on it is broken automatically.
    /// </summary>
    void SurfaceClosed(string surfaceId);

    /// <summary>
    /// The operator editing the wireframe directly: keeps the registry's copy — what an agent reads — in step with
    /// what is on screen. Raises <see cref="TextChanged"/>.
    /// </summary>
    void UpdateText(string surfaceId, string text);

    /// <summary>
    /// Raised whenever a surface's text changes, from the operator (<see cref="UpdateText"/>) or from a coupled
    /// agent, so its panel can re-render.
    /// </summary>
    event Action<string, string>? TextChanged;

    /// <summary>
    /// Raised on a coupling change (coupled, widened, or decoupled by close/session-end/operator Disconnect) so
    /// the surface can show or hide its "agent connected" bar.
    /// </summary>
    event Action<WireframeCouplingChange>? CouplingChanged;

    /// <summary>
    /// The operator's Disconnect on a surface: breaks the coupling at once, whatever capabilities it held.
    /// </summary>
    void Disconnect(string surfaceId);

    /// <summary>
    /// The surface's current source regardless of coupling, or null for an unknown surface — what the operator
    /// already sees on their own screen. Host-trusted: used to build a truthful consent prompt, never handed to an
    /// agent without that agent separately holding <see cref="WireframeCapability.Read"/>.
    /// </summary>
    string? PeekText(string surfaceId);

    // ---- Consumer side (the cockpit-wireframe MCP tools) ----

    /// <summary>
    /// The open surfaces as this agent session sees them, each with what this session already holds on it.
    /// </summary>
    IReadOnlyList<WireframeSurfaceView> ListSurfaces(string sessionId);

    /// <summary>
    /// Finds an open surface by its id or its operator-facing name, or null if there is no such surface.
    /// </summary>
    WireframeSurface? Resolve(string surfaceRef);

    /// <summary>
    /// What this session holds on the surface, or null when there is no coupling at all — so a caller can tell
    /// "never coupled" from "coupled, nothing granted yet".
    /// </summary>
    WireframeCoupling? CouplingOf(string sessionId, string surfaceId);

    /// <summary>
    /// Whether a <em>different</em> agent session holds any coupling on the surface, zero-capability included —
    /// exclusivity: a second agent is refused.
    /// </summary>
    bool IsCoupledByAnother(string sessionId, string surfaceId);

    /// <summary>
    /// Establishes a zero-capability coupling if this session holds none yet on the surface, idempotently. Throws
    /// for an unknown surface, or one coupled to a different session.
    /// </summary>
    void Couple(string sessionId, string surfaceId);

    /// <summary>
    /// Widens this session's coupling to also hold <paramref name="capability"/>, creating one first if needed;
    /// <see cref="WireframeCapability.Edit"/> grants <see cref="WireframeCapability.Read"/> alongside it. Throws
    /// for an unknown surface, or one coupled to a different session.
    /// </summary>
    void Grant(string sessionId, string surfaceId, WireframeCapability capability);

    /// <summary>
    /// The surface's current source, or null when this session does not hold
    /// <see cref="WireframeCapability.Read"/> on it.
    /// </summary>
    string? ReadCoupled(string sessionId, string surfaceId);

    /// <summary>
    /// Records that this session's read_wireframe just returned the source, so the panel can show when it was last
    /// read (AC-842). No-op when this session does not hold Read.
    /// </summary>
    void MarkRead(string sessionId, string surfaceId);

    /// <summary>
    /// Replaces the whole source and journals it as one <see cref="WireframeEditKind.Replace"/>, so even the
    /// wholesale write can be taken back — there is no diff gate on this surface. Refuses without
    /// <see cref="WireframeCapability.Edit"/>, and refuses a source the parser cannot read back.
    /// </summary>
    WireframeEditResult WriteCoupled(string sessionId, string surfaceId, string text);

    /// <summary>
    /// Applies one per-component edit under the registry's own lock, so the hold check, the line surgery and the
    /// "does this still parse" check all see one source and nothing is written unless all three pass — two edits
    /// naming different components therefore both land. Raises <see cref="TextChanged"/>,
    /// <see cref="ComponentEdited"/> and <see cref="HistoryChanged"/> when it lands.
    /// </summary>
    WireframeEditResult EditCoupled(string sessionId, string surfaceId, WireframeComponentEdit edit);

    /// <summary>
    /// Raised for each applied per-component edit with a one-line summary of what changed — what the activity
    /// strip (AC-848) shows per handling.
    /// </summary>
    event Action<string, string>? ComponentEdited;

    /// <summary>
    /// Breaks every coupling this agent session held (its session ended or crashed).
    /// </summary>
    void SessionEnded(string sessionId);

    // ---- An agent asking for a window (AC-835) ----

    /// <summary>
    /// Raised when an agent asked for a wireframe to be opened, after the operator approved it — whoever draws
    /// wireframe windows listens here.
    /// </summary>
    event Action<WireframeOpenRequest>? OpenRequested;

    /// <summary>
    /// Announces <paramref name="request"/> and remembers its caller, so the surface is coupled to it the moment
    /// the window registers it. False when nothing is listening at all — there is no wireframe surface in this
    /// cockpit to open one on.
    /// </summary>
    bool RequestOpen(WireframeOpenRequest request);

    // ---- Undo (AC-853): the safety net that stands in for the diff gate ----

    /// <summary>
    /// This surface's journaled edits, oldest first — both origins, so the activity strip (AC-848) can offer a
    /// targeted revert per line.
    /// </summary>
    IReadOnlyList<WireframeHistoryEntry> History(string surfaceId);

    /// <summary>
    /// Raised whenever a surface's history changes: a new edit journaled, or one marked reverted.
    /// </summary>
    event Action<string>? HistoryChanged;

    /// <summary>
    /// Undoes exactly the one journaled edit named by <paramref name="entryId"/> — never "the last change" — by
    /// putting back the lines it replaced, found in the source as it stands right now by their content rather than
    /// by the line numbers they had then. Null when it landed, else the refusal reason.
    /// </summary>
    string? Revert(string surfaceId, string entryId);

    // ---- The operator's "jij bewerkt" hold (AC-841) ----

    /// <summary>
    /// Marks a component as the operator's while they are editing it, by its line number. Idempotent.
    /// </summary>
    void HoldComponent(string surfaceId, int line);

    /// <summary>
    /// Releases the operator's hold on a component.
    /// </summary>
    void ReleaseComponent(string surfaceId, int line);

    /// <summary>
    /// Whether the operator is holding that component right now — an agent edit naming it is refused with a reason
    /// rather than applied or silently dropped.
    /// </summary>
    bool IsHeldByOperator(string surfaceId, int line);
}
