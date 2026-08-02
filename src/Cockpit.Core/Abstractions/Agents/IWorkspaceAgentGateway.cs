namespace Cockpit.Core.Abstractions.Agents;

/// <summary>One AI-session pane as the agent coordination line reports it: what <c>list_agents</c> and the coordinator's roster key on and describe a sibling by.</summary>
/// <param name="PaneId">The pane's stable id — the value of its own <c>COCKPIT_PANE_ID</c>.</param>
/// <param name="Name">The name shown on the pane's tab/sidebar row.</param>
/// <param name="Profile">The profile label the session was started under, or null before it is known.</param>
/// <param name="Statusline">The free-text line the session set via <c>cockpit-session__set_status</c>, or empty when none is set.</param>
/// <param name="DeliversAtTurnStart">
/// Whether a message addressed to this pane reaches it on its own, carried by its next turn (AC-394), or only when
/// that pane thinks to call <c>read_inbox</c>. Required rather than defaulted on purpose: a pane kind added later
/// has to answer it, and a default would answer for it — wrongly and silently, in whichever direction the default
/// happened to be written.
/// </param>
public sealed record WorkspaceAgentPane(string PaneId, string Name, string? Profile, string Statusline, bool DeliversAtTurnStart);

/// <summary>A caller's workspace as the agent coordination line sees it: which workspace it is, and every AI-session pane sharing it (the caller included).</summary>
/// <param name="WorkspaceId">
/// The workspace this caller's own pane resolved to. This is the boundary <see cref="IWorkspaceAgentGateway"/> itself
/// enforces — only panes sharing it are ever included in <paramref name="Panes"/> — not something
/// <see cref="IWorkspaceAgentCoordinator"/>'s roster partitions by; that roster is keyed on pane id alone and does
/// not know which workspace a pane is in at all.
/// </param>
/// <param name="Panes">Every AI-session pane in this workspace, in no particular order.</param>
public sealed record WorkspaceAgentSnapshot(string WorkspaceId, IReadOnlyList<WorkspaceAgentPane> Panes);

/// <summary>
/// Resolves the workspace a pane belongs to, host-side over the running workspaces (AC-391). There is no
/// pre-existing "find the workspace for this pane id" — the live workspaces are an App-layer concept
/// (<c>WorkspacesViewModel</c>/<c>CockpitViewModel</c>), which Infrastructure cannot reference — so this gateway is
/// the seam, the same way <c>IVerifySessionGateway</c> (AC-86) is the seam for a session's working directory.
/// <para>
/// The workspace is always derived from the pane, never accepted as a parameter: an agent names itself only by the
/// pane id the transport already verified it as, and gets back whichever workspace that pane actually sits in — it
/// cannot ask to see another workspace by naming one.
/// </para>
/// </summary>
public interface IWorkspaceAgentGateway
{
    /// <summary>
    /// The workspace <paramref name="paneId"/> belongs to, and every AI-session pane in it — or null when
    /// <paramref name="paneId"/> names no live session (it closed, or never existed), when that pane is not
    /// itself an agent session (a plain terminal pane also carries a pane id and an MCP key, but has no CLI on
    /// the other end to read a tool result — it must not be able to enroll itself or pollute a workspace's
    /// roster), or when the pane resolves to no workspace at all (no explicit one, and no Sessions workspace
    /// exists to fall back to) — reporting an invented empty workspace there would describe a desk that does not
    /// exist.
    /// </summary>
    Task<WorkspaceAgentSnapshot?> GetWorkspaceSnapshotAsync(string paneId);

    /// <summary>
    /// Starts a turn on <paramref name="targetPaneId"/> carrying a labelled notice that
    /// <paramref name="callerPaneId"/> marked a message to it as urgent (AC-395), and says what became of the
    /// attempt.
    /// <para>
    /// Every reason a wake can be refused is decided <em>here</em>, on the UI thread, against the panes as they
    /// are at that moment — not against the snapshot the sending tool checked a few instructions earlier. The
    /// recipient can go busy, open a consent banner or leave the desk in that window, and each of those turns a
    /// permitted wake into one of the three things a wake must never be: an interruption, a question pushed off
    /// the screen, or a turn on a pane in another workspace.
    /// </para>
    /// <para>
    /// Consent is the one check that is <em>not</em> here. It is a fact about the pane rather than about the
    /// moment, it costs no dispatch to read, and deciding it before this call keeps a session that never opted in
    /// from having a turn composed for it at all.
    /// </para>
    /// </summary>
    Task<AgentWakeOutcome> TryWakeAsync(string callerPaneId, string targetPaneId, string kind);
}

/// <summary>
/// What became of one wake — recorded on the append-only trail for every urgent message, and handed back to the
/// sender so "urgent" never quietly means "ignored".
/// </summary>
public enum AgentWakeOutcome
{
    /// <summary>A turn was started on the recipient, carrying the labelled wake notice.</summary>
    Woken,

    /// <summary>The recipient has not opted in to being woken. The message is delivered and waiting; nothing was started.</summary>
    NotOptedIn,

    /// <summary>
    /// The identical message was already waiting unread, so this send added nothing and nothing was woken. A wake
    /// fires when a message arrives, not every time a sender says it again — otherwise re-sending in a loop is a
    /// loop of turns on someone else's session.
    /// </summary>
    AlreadyWaiting,

    /// <summary>The recipient was working — a turn in flight, or background work still running. The message waits.</summary>
    Busy,

    /// <summary>
    /// The recipient has a question open in front of its operator. A turn started now would push a decision a
    /// human is standing at off the screen, and nothing an agent calls urgent outranks that.
    /// </summary>
    AwaitingOperator,

    /// <summary>The recipient's session could not take a turn at all — it has not started, or has already ended.</summary>
    CannotTakeATurn,

    /// <summary>The recipient is no longer a live pane the cockpit can find.</summary>
    PaneGone,

    /// <summary>The recipient is not on the caller's desk any more — the boundary, re-checked at the moment of waking.</summary>
    NotOnDesk,

    /// <summary>The attempt threw. The message is delivered either way; only the turn did not happen.</summary>
    Failed,

    /// <summary>
    /// The sender has woken agents as often in the last window as one session may (AC-396), so no turn was started.
    /// The message is delivered and waiting. This is the one refusal in this list that is about the <em>sender</em>
    /// rather than the recipient: everything else here says something about the pane being woken, and this says the
    /// caller is going too fast. Appended last so the values already on the trail keep meaning what they meant.
    /// </summary>
    RateLimited,
}
