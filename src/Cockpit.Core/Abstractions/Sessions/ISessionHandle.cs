using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// One live session pane, SDK or TTY, as the backend reaches it without a view model (AC-1373).
/// Reads may come from any thread; whoever supplies the handle marshals whatever needs its own thread.
/// </summary>
public interface ISessionHandle
{
    /// <summary>
    /// The pane id, the key everything else about a session is filed under.
    /// </summary>
    string PaneId { get; }

    /// <summary>
    /// The name the operator sees for this session.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// The workspace (desk) the pane sits on.
    /// </summary>
    string WorkspaceId { get; }

    /// <summary>
    /// The desk the pane counts as sitting on: its own <see cref="WorkspaceId"/>, else the first Sessions desk (AC-543).
    /// Null for a pane that belongs to no desk.
    /// </summary>
    string? PlacedWorkspaceId { get; }

    /// <summary>
    /// The directory the session works in, or null when it has none yet.
    /// </summary>
    string? WorkingDirectory { get; }

    /// <summary>
    /// The branch of the worktree the cockpit made for this session, or null.
    /// </summary>
    string? WorktreeBranch { get; }

    /// <summary>
    /// The label of the profile the session runs under.
    /// </summary>
    string? ActiveProfileLabel { get; }

    /// <summary>
    /// True for a plain shell pane, which has no agent behind it.
    /// </summary>
    bool IsTerminal { get; }

    /// <summary>
    /// True for a pane a plugin embeds in its own surface (an Autopilot step, say), which the session grid never lists.
    /// </summary>
    bool IsEmbedded { get; }

    /// <summary>
    /// The coarse lifecycle state the sidebar dot shows.
    /// </summary>
    SessionStatus SessionStatus { get; }

    /// <summary>
    /// What the session last said it is working on; empty when nothing was set.
    /// </summary>
    string Statusline { get; }

    /// <summary>
    /// Whether a prompt sent now would be taken rather than refused.
    /// </summary>
    bool CanTakeAPrompt { get; }

    /// <summary>
    /// Whether the session's own next turn carries its agent inbox.
    /// </summary>
    bool DeliversInboxAtTurnStart { get; }

    /// <summary>
    /// Whether a prompt is held until the session is ready to take it.
    /// </summary>
    bool HasPromptWaitingToBeDelivered { get; }

    /// <summary>
    /// Whether a shell the session backgrounded is still running; asked where the session keeps its task list.
    /// </summary>
    Task<bool> HasOutstandingBackgroundShellsAsync();

    /// <summary>
    /// Whether a consent banner is waiting for the operator on this pane.
    /// </summary>
    bool HasPendingConsent { get; }

    /// <summary>
    /// The number of live processes in the session's tree.
    /// </summary>
    int ProcessCount { get; }

    /// <summary>
    /// The CPU the session's process tree uses, in percent.
    /// </summary>
    double ProcessCpuPercent { get; }

    /// <summary>
    /// The memory the session's process tree uses, in bytes.
    /// </summary>
    long ProcessMemoryBytes { get; }

    /// <summary>
    /// The processes the session left running after the work that started them ended.
    /// </summary>
    int AbandonedProcessCount { get; }

    /// <summary>
    /// The last <paramref name="count"/> transcript rows, oldest first, with the total the transcript holds.
    /// Empty for a plain terminal, which has no agent to have written anything.
    /// </summary>
    Task<SessionTranscriptSlice> ReadTranscriptAsync(int count);

    /// <summary>
    /// Sends <paramref name="prompt"/> as the operator's next message; false when the session cannot take it.
    /// </summary>
    Task<bool> SendPromptAsync(string prompt);

    /// <summary>
    /// Submits <paramref name="prompt"/> now (true) or holds it until the session is ready (false), in the same step
    /// as checking that no earlier prompt is still held; null, accepting nothing, when one is (AC-1375).
    /// </summary>
    Task<bool?> SubmitPromptWhenReadyAsync(string prompt);

    /// <summary>
    /// Records <paramref name="branch"/> as the branch of the worktree this session now owns (AC-719).
    /// </summary>
    Task SetWorktreeBranchAsync(string? branch);

    /// <summary>
    /// Answers the pending permission prompt for <paramref name="toolUseId"/>; false when no such prompt is open.
    /// </summary>
    Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow);

    /// <summary>
    /// Hands a verify render back to the session as its next input; false when the session cannot take it.
    /// </summary>
    Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng);

    /// <summary>
    /// Every Allow/Deny question this session is stopped on right now (AC-1324); empty for a TTY session or a
    /// plain terminal, which have no tool-permission rows of their own.
    /// </summary>
    Task<IReadOnlyList<SessionPendingPermission>> ReadPendingPermissionsAsync();

    /// <summary>
    /// Sets what the session says it is working on; true when a live pane took it (AC-13).
    /// </summary>
    Task<bool> SetStatuslineAsync(string statusline);

    /// <summary>
    /// Proposes <paramref name="name"/> as the session's name; false when the operator already named it on
    /// purpose, which leaves the existing name standing (AC-310).
    /// </summary>
    Task<bool> SuggestNameAsync(string name);

    /// <summary>
    /// The three fields a wake decision reads as one instant, not as three separate moments a consent banner or a
    /// turn starting could fall between (AC-1374). Use this, not the individual properties, wherever a decision
    /// chains more than one of them.
    /// </summary>
    Task<SessionWakeState> ReadWakeStateAsync();
}

// AC-1324: one open Allow/Deny question on a session, as its own handle reports it — the same shape
// IAssistantReadGateway's AssistantPendingPermission carries minus the pane id, which the caller already has.
public sealed record SessionPendingPermission(string ToolUseId, string ToolName, string InputJson, DateTimeOffset SinceUtc);

// AC-1374: HasPendingConsent, SessionStatus and CanTakeAPrompt, read together — see ISessionHandle.ReadWakeStateAsync.
public sealed record SessionWakeState(bool HasPendingConsent, SessionStatus SessionStatus, bool CanTakeAPrompt);
