namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The host-side spawn service (AC-545): the one place a session is started, stopped or a desk created on an
/// agent's behalf. Infrastructure hosts the tools that call it; the App implements it, because starting a session
/// is the shell's own machinery.
/// </summary>
/// <remarks>
/// <b>This interface executes; it does not scope.</b> Every method that places something on a desk takes a
/// <see cref="SpawnTarget"/>, which can only be built through one of the two named doors on that type — and those
/// doors are the two scoping rules. Nothing here derives a workspace, validates one, or falls back to a default
/// desk: a caller arrives with a target it is entitled to, or it does not call. Read <see cref="SpawnTarget"/>
/// before adding a caller; the note there about the strict rule not being a filter on the permissive one is the
/// design, not a preference.
/// <para>
/// <b>Never throws for an outcome the caller can be told about.</b> A refused spawn, a workspace that has since
/// closed, a profile that does not exist: all of those come back as a result with a reason on it, because the
/// caller is an agent whose next sentence is spoken to the operator. An exception here would surface as "the tool
/// failed", which is the one answer that helps nobody.
/// </para>
/// </remarks>
public interface IAssistantAgentGateway
{
    /// <summary>
    /// Starts a session on <paramref name="request"/>'s profile and places it on the target's desk as an ordinary
    /// pane — the same kind of pane the New-session dialog produces, visible in the grid and in the sidebar, with
    /// its own consents and its own transcript.
    /// </summary>
    /// <remarks>
    /// The desk is <em>not</em> activated. A spawn into a workspace the operator is not looking at must leave them
    /// where they are: the assistant is often asked to set work up somewhere else precisely so it does not
    /// interrupt what is on screen.
    /// </remarks>
    Task<AgentSpawnResult> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the session on <paramref name="paneId"/>, whichever desk it is on. Refuses a pane that is not an
    /// agent session, one that does not exist, and the assistant's own — the assistant does not get to end itself
    /// mid-sentence, and a pane id it happens to know is not a licence to.
    /// </summary>
    Task<AgentStopResult> StopAsync(string paneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames the session on <paramref name="paneId"/> — the title in its header and its sidebar row — exactly as
    /// an inline rename does, so it counts as a name somebody chose rather than one a later suggestion may replace.
    /// </summary>
    /// <remarks>
    /// Refuses the same three things <see cref="StopAsync"/> does and for the same reasons: a pane that is not
    /// there, one that runs inside a workspace's own surface rather than as a pane, and the assistant's own.
    /// </remarks>
    Task<AssistantRenameResult> RenameSessionAsync(string paneId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames the workspace on <paramref name="workspaceId"/> — the tab label the operator reads — and persists it.
    /// The desk is not activated: renaming one is not the same as walking to it.
    /// </summary>
    Task<AssistantRenameResult> RenameWorkspaceAsync(string workspaceId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every workspace the cockpit has open, so a spoken name ("the Cockpit desk") can be turned into the id a
    /// spawn needs. Includes desks with nothing running on them, which is the half a session roster cannot show.
    /// </summary>
    Task<IReadOnlyList<AssistantWorkspaceRow>> ListWorkspacesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The profiles a spawn may name, with the provider and model each one runs on.
    /// </summary>
    /// <remarks>
    /// A spawn's profile is required and never defaulted, which left the assistant with one way to discover the
    /// labels: guess one and read them off the refusal. Asking "which profile?" is right; asking it while unable to
    /// say what the choices are — or while reciting all of them when the operator already said "a Claude one" — is
    /// a question they have to do the work for. This is what makes the difference between the two.
    /// </remarks>
    Task<IReadOnlyList<AssistantProfileRow>> ListProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a Sessions workspace called <paramref name="name"/> and returns it. Unlike a spawn, this one does
    /// bring the operator to the new desk: asking for a workspace to be made is asking to be shown it, and there
    /// is nothing on it yet to interrupt.
    /// </summary>
    Task<AssistantWorkspaceRow?> CreateWorkspaceAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the workspace on <paramref name="workspaceId"/> — the counterpart of
    /// <see cref="CreateWorkspaceAsync"/>, and the same act as the operator's own ✕ on the tab.
    /// </summary>
    /// <remarks>
    /// <b>Only an empty desk.</b> Refuses while anything is still placed there, so the sessions a close would take
    /// with it are stopped deliberately, one at a time and each with its own approval, rather than swept up by a
    /// call the operator approved for a desk. That refusal is a guarantee and not a nicety: the confirmation dialog
    /// behind the ✕ exists to name what is about to be lost, and a tool that cannot show a dialog has to earn the
    /// same safety by refusing instead. Also refuses the last desk and the projects overview, which is where
    /// <c>WorkspacesViewModel.CanClose</c> already says no — the button greys out for both.
    /// </remarks>
    Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a message in the inbox of the agent session on <paramref name="paneId"/>, whichever desk it is on — the
    /// same inbox <c>cockpit-agents</c>' own <c>notify</c> delivers into, so a recipient reads it with the same
    /// <c>read_inbox</c> and the same turn-start delivery it already has.
    /// </summary>
    /// <remarks>
    /// <b>Why this reaches every desk when <c>notify</c> reaches one.</b> <c>notify</c>'s rule is that a sender may
    /// address only its own desk, enforced on the host's answer to "who is on the caller's desk". The assistant sits
    /// on no desk at all — the same structural fact AC-544/AC-545 were shaped around — so that rule does not narrow
    /// it, it excludes it outright. This is the assistant's own door, and it is the assistant's alone: nothing here
    /// relaxes the check on <c>notify</c>, which still refuses every agent that reaches past its own desk.
    /// <para>
    /// Nothing is woken. <c>notify</c>'s <c>urgent</c> spends the recipient operator's money on a turn they did not
    /// ask for, which is a different weight of act from leaving a note; the assistant's message is delivered and
    /// waits, exactly as an ordinary <c>notify</c> does.
    /// </para>
    /// </remarks>
    Task<AgentMessageResult> SendMessageAsync(string paneId, string kind, string body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits <paramref name="prompt"/> as a turn in the agent session on <paramref name="paneId"/>, whichever desk
    /// it is on — the text goes in <em>and</em> is sent, which is what separates this from a message.
    /// </summary>
    /// <remarks>
    /// Refuses the same three things <see cref="StopAsync"/> refuses, for the same reasons: the assistant's own
    /// session, a pane that is not an agent session, and a pane that does not exist or runs inside a workspace's own
    /// surface rather than as a pane.
    /// <para>
    /// Delivery rides <c>SessionPanelViewModel.SubmitPromptWhenReady</c>, so a session that has only just been
    /// started holds the turn until it can take one instead of dropping it —
    /// <see cref="AgentPromptResult.Delivered"/> says which of the two happened, and is never true for a turn that
    /// is still waiting.
    /// </para>
    /// </remarks>
    Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Arms the host-side session watcher (AC-640) on <paramref name="paneId"/> for one or more of the five event
    /// kinds, so the assistant is told when that session finishes, stalls, disappears or prints something it asked
    /// to hear about — instead of polling <c>list_sessions</c> for it.
    /// </summary>
    /// <remarks>
    /// <b>Why arming is per pane rather than automatic.</b> <c>CiWatcher</c> watches every live checkout because
    /// every open checkout is worth checking; a session's status is not. The operator starts sessions the assistant
    /// was never asked to follow, and reporting on those would be the cockpit answering a question nobody asked.
    /// <para>
    /// Refuses rather than throws, like everything else here: a pane id that resolves to nothing, <c>stuck</c> or
    /// <c>pattern</c> on a pane that keeps no transcript in the cockpit, and a <c>pattern</c> that is not a valid
    /// regular expression — the last one at arm time rather than on the first tick, where nobody would see it.
    /// </para>
    /// </remarks>
    /// <param name="paneId">The session to watch, as <c>list_sessions</c> reports it.</param>
    /// <param name="events">Which of the five kinds to watch for; an unknown one is refused.</param>
    /// <param name="afterMinutes">How long without a new transcript row counts as stuck, or null for the default.</param>
    /// <param name="pattern">The regular expression the <c>pattern</c> event matches new transcript rows against.</param>
    Task<AssistantWatchResult> WatchSessionAsync(
        string paneId,
        IReadOnlyList<string>? events,
        int? afterMinutes = null,
        string? pattern = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disarms the watch on <paramref name="paneId"/>. True when one was armed; false says there was nothing to
    /// stop, which is worth reporting rather than dressing up as a stop that happened.
    /// </summary>
    /// <remarks>
    /// A pane the watcher itself finds gone disarms on its own, so this is for the ordinary case — the assistant
    /// stopping a session it started, or losing interest in one it armed.
    /// </remarks>
    Task<bool> UnwatchSessionAsync(string paneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-owns the worktree at <paramref name="path"/> — currently the assistant's own — onto
    /// <paramref name="paneId"/> (AC-719 ronde B). Refuses when the worktree is not the assistant's, or when
    /// <paramref name="paneId"/> names no running agent session or the assistant itself.
    /// </summary>
    Task<WorktreeHandoverResult> HandoverWorktreeAsync(string path, string paneId, CancellationToken cancellationToken = default);
}

// What came of a handover. Same shape and same reason as `AgentStopResult` — a refusal is a sentence the
// assistant says, not an exception it fails on.
public sealed record WorktreeHandoverResult(bool Ok, string? Path, string? Branch, string? SessionName, string? Error)
{
    public static WorktreeHandoverResult HandedOver(string path, string branch, string sessionName) =>
        new(true, path, branch, sessionName, null);

    public static WorktreeHandoverResult Refused(string error) => new(false, null, null, null, error);
}

// What came of arming a watch. Same shape and same reason as `AgentStopResult`: a refusal is a sentence the
// assistant says out loud, not an exception it fails on.
//
// `Name`: the watched session's title, so the confirmation names what is being watched rather than a pane id nobody
// can check by ear.
public sealed record AssistantWatchResult(bool Ok, string? Name, string? Error)
{
    public static AssistantWatchResult Watched(string name) => new(true, name, null);

    public static AssistantWatchResult Refused(string error) => new(false, null, error);
}

// What came of a message. Same shape and same reason as `AgentStopResult`.
//
// `MessageId`: The id the message is waiting under, so a repeat send can be recognised as the same one.
// `Deduplicated`: True when the identical message was already waiting unread: this call added nothing, and `MessageId` is the one that was already there.
// `DeliversAtTurnStart`:
// Whether the recipient will see this without going to look. Reported because "delivered" on a pane with no passive
// delivery means the message is waiting, not that anyone has been told — and an assistant that then reports "I told
// them" to the operator has said something untrue on the strength of a field that read like success.
public sealed record AgentMessageResult(
    bool Ok,
    string? PaneId,
    string? SessionName,
    string? MessageId,
    bool Deduplicated,
    bool DeliversAtTurnStart,
    string? Error)
{
    public static AgentMessageResult Sent(string paneId, string sessionName, string messageId, bool deduplicated, bool deliversAtTurnStart) =>
        new(true, paneId, sessionName, messageId, deduplicated, deliversAtTurnStart, null);

    public static AgentMessageResult Refused(string error) => new(false, null, null, null, false, false, error);
}

// What came of a prompt. Same shape and same reason as `AgentStopResult`.
//
// `Delivered`:
// True when the turn was submitted on the spot. False means the session cannot take one yet — it is still coming up
// — and the turn is being held for it. Not an error, and not a delivery either: the difference is the whole reason
// this field exists, because an assistant that says "sent" for a turn that is still waiting has reported work that
// has not started.
public sealed record AgentPromptResult(bool Ok, string? PaneId, string? SessionName, bool Delivered, string? Error)
{
    public static AgentPromptResult Handed(string paneId, string sessionName, bool delivered) =>
        new(true, paneId, sessionName, delivered, null);

    public static AgentPromptResult Refused(string error) => new(false, null, null, false, error);
}

// What came of removing a workspace. Same shape and same reason as `AgentStopResult`.
//
// `Name`: The tab label the desk had, so the confirmation names what the operator will see disappear.
public sealed record WorkspaceRemovalResult(bool Ok, string? Name, string? Error)
{
    public static WorkspaceRemovalResult Removed(string name) => new(true, name, null);

    public static WorkspaceRemovalResult Refused(string error) => new(false, null, error);
}

// One session to start: where it goes, what it runs, and what it is handed to begin with.
//
// `Target`: The desk and the rule that chose it. See `SpawnTarget`.
// `ProfileLabel`:
// The profile to run under, by its label. Required and never defaulted: which profile a session runs under decides
// its provider, its model and therefore its cost, and an agent that did not have to say so would spawn Opus workers
// by accident (AC-436 guardrail 6, which holds for whatever the assistant starts too).
// `Prompt`: The first message to hand the session once it is up, or null to leave it waiting.
// `WorkingDirectory`: The folder to run in, or null for the profile's own default.
// `SessionName`: What to call the pane, or null to let the profile and the clock name it.
// `Kind`:
// The route to start on — "sdk" or "tty" — or null for the one the profile is set to, which is what nearly every
// spawn should use.
// *Why the route is here at all.* The New-session dialog has a Kind toggle, so "the same profile, but as an
// SDK session" is an ordinary thing to want. Without a way to say it here, that request has nowhere to land — and
// what an assistant does with a request it has no tool for is reach for the nearest thing that sounds close. Asked
// for exactly this, it went looking through `cockpit-orchestrator`, which starts work with no pane, outside
// this ticket's consent gate and outside its trail. A missing parameter turned into a detour around the guardrail.
// `OptionOverrides`:
// Provider option keys to start this one session with, on top of the profile's own defaults (AC-648) — "that profile,
// but at low effort". Per key: what is not named keeps the profile's value, so naming `effort` never costs the profile
// its own `permission-mode`. Validated against what the profile's provider declares (AC-649), and `permission-mode`
// — with any other provider's word for the same launch-time access-control question — is refused outright, whoever
// asks. See `SpawnOptionOverrides`. One key is the host's own rather than a provider's: `cockpit.memory-cap-mb`
// (AC-661) sets how much memory this session's whole process tree may hold before the OS cuts it off.
// `IsolateInWorktree` (AC-719):
// Tri-state — left out inherits the resolved project's own default, `true` may isolate on top of it, and `false`
// is refused before a launch is composed: overruling isolation *away* would run it in the operator's real checkout.
public sealed record AgentSpawnRequest(
    SpawnTarget Target,
    string ProfileLabel,
    string? Prompt = null,
    string? WorkingDirectory = null,
    string? SessionName = null,
    string? Kind = null,
    IReadOnlyDictionary<string, string>? OptionOverrides = null,
    bool? IsolateInWorktree = null);

// What came of a spawn. A refusal carries `Error` and no pane; both are reported to the agent, so a
// spawn that could not happen is a sentence the operator hears rather than a session that silently is not there.
public sealed record AgentSpawnResult(bool Ok, string? PaneId, string? SessionName, string? WorkingDirectory, string? Error)
{
    public static AgentSpawnResult Started(string paneId, string sessionName, string? workingDirectory) =>
        new(true, paneId, sessionName, workingDirectory, null);

    public static AgentSpawnResult Refused(string error) => new(false, null, null, null, error);
}

// What came of a stop. Same shape and same reason as `AgentSpawnResult`.
public sealed record AgentStopResult(bool Ok, string? PaneId, string? SessionName, string? Error)
{
    public static AgentStopResult Stopped(string paneId, string sessionName) => new(true, paneId, sessionName, null);

    public static AgentStopResult Refused(string error) => new(false, null, null, error);
}

// What came of a rename: the name that now stands, or the reason it does not. Same shape and same reason as
// `AgentStopResult` — a refusal is a sentence the assistant says, not an exception it fails on.
public sealed record AssistantRenameResult(bool Ok, string? Name, string? Error)
{
    public static AssistantRenameResult Renamed(string name) => new(true, name, null);

    public static AssistantRenameResult Refused(string error) => new(false, null, error);
}

// One desk, as the assistant may see it: enough to name it back to the operator and to spawn onto it.
//
// `Id`: The workspace id — what a spawn actually takes.
// `Name`: The tab label the operator sees, which is the name they will say out loud.
// `Type`: The workspace type id ("sessions", "dashboard", "projects", a plugin's own).
// `CanHostSessions`:
// Whether a session may be placed here at all. Only a Sessions desk can show one — a dashboard would run it
// invisibly, which is worse than refusing — so this is reported rather than left for the assistant to infer from
// `Type`.
// `SessionCount`: How many agent sessions are on it right now.
// `IsActive`: Whether this is the desk the operator is looking at.
public sealed record AssistantWorkspaceRow(
    string Id,
    string Name,
    string Type,
    bool CanHostSessions,
    int SessionCount,
    bool IsActive);

// One profile a spawn may name.
//
// `Label`: Exactly what `start_agent` takes — the label, not the display string.
// `Provider`: Which provider it runs on ("Claude", "LM Studio"), so "a Claude one" can be resolved without guessing from the label's wording.
// `Model`: The model, where the profile pins one. Null means the provider's own default, which is worth saying rather than showing as blank.
public sealed record AssistantProfileRow(string Label, string Provider, string? Model)
{
    // What this profile is actually configured to run at, in its provider's own vocabulary (AC-647) — read from
    // the provider's declared option schema, so a raw settings dump never has to be guessed at. Empty for a
    // provider that declares nothing; that is an answer, not a gap to fill with another provider's fields.
    public IReadOnlyList<AssistantProfileOptionRow> Options { get; init; } = [];
}

// One option a profile's provider understands, with what this profile sets it to (AC-647).
//
// `Key`: The provider's own option key, e.g. `permission-mode` or `sandbox` — what `start_agent` will one day take.
// `Label`: What the option is called in the provider's own words, for reading out loud.
// `Value`: The raw value in force. Null when the profile sets none and the provider names no default.
// `ValueLabel`: That value in the provider's own words ("Bypass permissions"), or the raw value when it has no friendlier one.
// `SetOnProfile`: Whether the profile itself sets this, or it is only the provider's default — the difference between what was chosen and what merely applies.
public sealed record AssistantProfileOptionRow(
    string Key,
    string Label,
    string? Value,
    string? ValueLabel,
    bool SetOnProfile);
