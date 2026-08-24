namespace Cockpit.Core.Abstractions.Agents;

// AC-1013: One AI-session pane as the agent coordination line reports it. DeliversAtTurnStart is required, not
// defaulted, so a pane kind added later must answer it explicitly rather than get a silently wrong default.
public sealed record WorkspaceAgentPane(string PaneId, string Name, string? Profile, string Statusline, bool DeliversAtTurnStart);

// AC-1013: A caller's workspace as the agent coordination line sees it. WorkspaceId is the boundary
// `IWorkspaceAgentGateway` itself enforces on `Panes` — unlike the coordinator's roster, which is keyed on pane id alone.
public sealed record WorkspaceAgentSnapshot(string WorkspaceId, IReadOnlyList<WorkspaceAgentPane> Panes);

/// <summary>
/// Resolves the workspace a pane belongs to, host-side over the running workspaces (AC-391); live workspaces are an
/// App-layer concept Infrastructure cannot reference, so this gateway is the seam, the same way
/// <c>IVerifySessionGateway</c> (AC-86) is for a working directory. Always derived from the pane, never accepted as a parameter — an agent cannot ask to see another workspace by naming one.
/// </summary>
public interface IWorkspaceAgentGateway
{
    /// <summary>
    /// The workspace <paramref name="paneId"/> belongs to, and every AI-session pane in it — or null when
    /// <paramref name="paneId"/> names no live session, when that pane isn't itself an agent session (a plain terminal
    /// pane carries a pane id and MCP key too, but has no CLI to read a tool result, so it must not enroll itself), or when the pane resolves to no workspace — an invented empty one would describe a desk that doesn't exist.
    /// </summary>
    Task<WorkspaceAgentSnapshot?> GetWorkspaceSnapshotAsync(string paneId);

    /// <summary>
    /// Starts a turn on <paramref name="targetPaneId"/> carrying a labelled notice that <paramref name="callerPaneId"/>
    /// marked a message urgent (AC-395), and says what became of it. Every refusal is decided <em>here</em>, against
    /// the panes right now rather than an earlier snapshot, so a recipient that goes busy, opens a consent banner, or leaves never gets an interruption. Consent is checked earlier, keeping a never-opted-in session untouched.
    /// </summary>
    Task<AgentWakeOutcome> TryWakeAsync(string callerPaneId, string targetPaneId, string kind);

    /// <summary>
    /// Starts a turn on <paramref name="targetPaneId"/> because mail from <paramref name="fromPaneId"/> is waiting in
    /// its inbox (AC-656) — the host delivering a pane's own already-accepted mail, not a peer interrupting it. Checks
    /// everything <see cref="TryWakeAsync"/> does except the desk boundary, already enforced at delivery time. Consent-free by design — see the ticket for why this isn't <see cref="TryWakeAsync"/> with the check skipped by a flag.
    /// </summary>
    Task<AgentWakeOutcome> TryWakeForWaitingMailAsync(string fromPaneId, string targetPaneId, string kind);
}

// What became of one wake — recorded on the append-only trail for every urgent message, and handed back to the
// sender so "urgent" never quietly means "ignored".
public enum AgentWakeOutcome
{
    // A turn was started on the recipient, carrying the labelled wake notice.
    Woken,

    // The recipient has not opted in to being woken. The message is delivered and waiting; nothing was started.
    NotOptedIn,

    // AC-1013: Identical message already waiting unread, so nothing was woken — a wake fires when a message
    // arrives, not every re-send, or a resend loop becomes a loop of turns on someone else's session.
    AlreadyWaiting,

    // The recipient was working — a turn in flight, or background work still running. The message waits.
    Busy,

    // The recipient has a question open in front of its operator. A turn started now would push a decision a
    // human is standing at off the screen, and nothing an agent calls urgent outranks that.
    AwaitingOperator,

    // The recipient's session could not take a turn at all — it has not started, or has already ended.
    CannotTakeATurn,

    // The recipient is no longer a live pane the cockpit can find.
    PaneGone,

    // The recipient is not on the caller's desk any more — the boundary, re-checked at the moment of waking.
    NotOnDesk,

    // The attempt threw. The message is delivered either way; only the turn did not happen.
    Failed,

    // AC-1013: Sender hit its wake rate limit (AC-396) — the one refusal here about the sender, not the
    // recipient. Appended last so values already on the trail keep meaning what they meant.
    RateLimited,
}
