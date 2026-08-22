namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The host-side spawn service (AC-545): the one place a session is started, stopped or a desk created on an
/// agent's behalf. Executes but does not scope — every method takes a <see cref="SpawnTarget"/>, buildable only
/// through its two named doors, never deriving/defaulting a workspace. Never throws for a tellable outcome (refused
/// spawn, closed workspace, missing profile): comes back as a result with a reason, since "tool failed" helps nobody.
/// </summary>
public interface IAssistantAgentGateway
{
    /// <summary>
    /// Starts a session on <paramref name="request"/>'s profile and places it on the target's desk as an ordinary
    /// pane — visible in the grid and sidebar, with its own consents and transcript. The desk is <em>not</em>
    /// activated: a spawn elsewhere must leave the operator where they are, since work is often set up on another
    /// desk precisely so it does not interrupt what is on screen.
    /// </summary>
    Task<AgentSpawnResult> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the session on <paramref name="paneId"/>, whichever desk it is on. Refuses a pane that is not an
    /// agent session, one that does not exist, and the assistant's own — the assistant does not get to end itself
    /// mid-sentence, and a pane id it happens to know is not a licence to.
    /// </summary>
    /// <param name="paneId">The session to close.</param>
    /// <param name="caller">
    /// Who is closing it, for the audit trail. Defaults to the assistant, which was the only caller until AC-795
    /// gave a paired controller its own tools — a stop that arrived from another machine and is written down as
    /// this cockpit's own assistant is a trail that reads plausibly and is wrong.
    /// </param>
    /// <param name="callerPaneId">The caller's verified pane, where it has one. Null for the assistant, which does not.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<AgentStopResult> StopAsync(
        string paneId,
        SpawnCaller caller = SpawnCaller.Assistant,
        string? callerPaneId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames the session on <paramref name="paneId"/> — the title in its header and its sidebar row — exactly as
    /// an inline rename does, so it counts as a name somebody chose rather than one a later suggestion may replace.
    /// Refuses the same three things <see cref="StopAsync"/> does: a pane that is not there, one that runs inside a
    /// workspace's own surface rather than as a pane, and the assistant's own.
    /// </summary>
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
    /// The profiles a spawn may name, with the provider and model each one runs on. A spawn's profile is required
    /// and never defaulted, which left the assistant with one way to discover the labels: guess one and read them
    /// off the refusal. Asking "which profile?" is right; asking while unable to say what the choices are — or
    /// while reciting all of them when the operator already said "a Claude one" — makes them do the work instead.
    /// </summary>
    Task<IReadOnlyList<AssistantProfileRow>> ListProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a Sessions workspace called <paramref name="name"/> and returns it. Unlike a spawn, this one does
    /// bring the operator to the new desk: asking for a workspace to be made is asking to be shown it, and there
    /// is nothing on it yet to interrupt.
    /// </summary>
    Task<AssistantWorkspaceRow?> CreateWorkspaceAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the workspace on <paramref name="workspaceId"/> — counterpart of <see cref="CreateWorkspaceAsync"/>.
    /// Refuses while anything is on it, the same guarantee as the ✕'s confirmation dialog (sessions stopped one at
    /// a time, never swept up) since no dialog can be shown here. Also refuses the last desk and projects overview.
    /// </summary>
    Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a message in the inbox of the agent session on <paramref name="paneId"/> — the same inbox
    /// <c>notify</c> delivers into, read via <c>read_inbox</c>/turn-start delivery. Reaches every desk, unlike
    /// <c>notify</c> (own desk only), since the assistant sits on no desk at all (AC-544/AC-545); its own door. Nothing is woken; delivery just waits.
    /// </summary>
    Task<AgentMessageResult> SendMessageAsync(string paneId, string kind, string body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits <paramref name="prompt"/> as a turn on <paramref name="paneId"/> — goes in <em>and</em> is sent,
    /// unlike a message. Refuses what <see cref="StopAsync"/> refuses: the assistant's own session, a non-agent
    /// pane, one that does not exist or runs inside a workspace's own surface. A just-started session holds the
    /// turn instead of dropping it — <see cref="AgentPromptResult.Delivered"/> says which happened, never true for a still-waiting turn.
    /// </summary>
    Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Arms the host-side session watcher (AC-640) on <paramref name="paneId"/> for one or more of the five event
    /// kinds, so the assistant is told when that session finishes, stalls, disappears or prints something it asked
    /// about — instead of polling <c>list_sessions</c>. Per pane rather than automatic: <c>CiWatcher</c> watches
    /// every live checkout, but a session's status is not always worth reporting on. Refuses rather than throws: a
    /// pane id that resolves to nothing, <c>stuck</c>/<c>pattern</c> with no transcript, or an invalid regex — the last checked at arm time, not on the first tick.
    /// </summary>
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
    /// stop, worth reporting rather than dressing up as a stop that happened. A pane the watcher itself finds gone
    /// disarms on its own, so this is for the ordinary case — stopping a session, or losing interest in one armed.
    /// </summary>
    Task<bool> UnwatchSessionAsync(string paneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-owns the worktree at <paramref name="path"/> — currently the assistant's own — onto
    /// <paramref name="paneId"/> (AC-719 ronde B). Refuses when the worktree is not the assistant's, or when
    /// <paramref name="paneId"/> names no running agent session or the assistant itself.
    /// </summary>
    Task<WorktreeHandoverResult> HandoverWorktreeAsync(string path, string paneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a shared project <c>list_shared_projects</c> offered into an ordinary local project (AC-798), through
    /// the same route the "Add to my projects…" dialog runs — never a write straight to the project store, which
    /// would duplicate its validation and could race the dialog. Three things never default here: the folder on
    /// this machine, the profile to run under, and one local reference per machine-specific resource row; a missing
    /// one is refused with the question in it. It does not clone — the folder named must already exist.
    /// </summary>
    /// <param name="sharedProjectId">A <c>list_shared_projects</c> id, whose <c>{scheme}:</c> prefix names the source it came from.</param>
    /// <param name="sourceDirectory">The folder on this machine the project's sessions run in. Must exist.</param>
    /// <param name="profileLabel">The profile its sessions default to, by label — the dialog's one required field.</param>
    /// <param name="resourceReferences">One local reference per machine-specific resource row, in the order the definition lists them.</param>
    Task<AssistantProjectBindResult> BindSharedProjectAsync(
        string sharedProjectId,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a brand-new local project (AC-799) — not bound to any shared definition, unlike
    /// <see cref="BindSharedProjectAsync"/> — through the same route "New project" runs. A name matching a project
    /// a connection already shares is refused rather than duplicated — <see cref="BindSharedProjectAsync"/> is
    /// likely the right door instead. Four parameters decide how every session runs, not merely how it is
    /// labelled: <paramref name="sourceDirectory"/>, <paramref name="enabledMcpServerNames"/>, <paramref name="isolateInWorktreeByDefault"/>, <paramref name="behaviorPrompt"/>.
    /// </summary>
    /// <param name="name">
    /// The project's display name — the dialog's one required field. Free to collide with another project's name.
    /// </param>
    /// <param name="description">
    /// Free-text note on what this project is. Null for none.
    /// </param>
    /// <param name="sourceDirectory">
    /// The folder its sessions start in. Must exist when given; null/blank for an administrative project with no
    /// folder of its own.
    /// </param>
    /// <param name="defaultProfileLabel">
    /// The profile its sessions start under, by label. Null leaves every session to name its own.
    /// </param>
    /// <param name="behaviorPrompt">
    /// Appended to every session's system prompt on top of its profile's own. Null appends nothing.
    /// </param>
    /// <param name="isolateInWorktreeByDefault">
    /// Whether new sessions here isolate in their own git worktree by default.
    /// </param>
    /// <param name="enabledMcpServerNames">
    /// Names of MCP servers this project's sessions start ticked. Null means no opinion — every offered server
    /// starts ticked, following the registry.
    /// </param>
    /// <param name="category">
    /// Which group this project sits under in the manager's list. Null/blank groups it under "Uncategorized".
    /// </param>
    /// <param name="pluginFields">
    /// What this project is called elsewhere, keyed by the field a plugin registered — e.g.
    /// <c>{"youtrack.project": "AC"}</c>, the same shape <c>list_projects</c> reports. A key no installed plugin
    /// registered is refused.
    /// </param>
    Task<AssistantProjectCreateResult> CreateProjectAsync(
        string name,
        string? description = null,
        string? sourceDirectory = null,
        string? defaultProfileLabel = null,
        string? behaviorPrompt = null,
        bool isolateInWorktreeByDefault = false,
        IReadOnlyList<string>? enabledMcpServerNames = null,
        string? category = null,
        IReadOnlyDictionary<string, string>? pluginFields = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a clarifying question to the operator as a card in the assistant's own chat window (AC-955) — radio
    /// buttons for one pick, checkboxes for several, an optional "Other, namely…" free-text row. Returns as soon as
    /// shown; does not wait for an answer, and nothing here decides whether or when the operator answers — the card
    /// sits in the transcript until <c>SubmitQuestionAnswersCommand</c> resolves it, a UI gesture this method
    /// cannot await. Refused only when there is nowhere to put the card, e.g. the assistant's own session is not running.
    /// </summary>
    /// <param name="question">The question, shown above the options.</param>
    /// <param name="options">2 to 6 choices, each a label and an optional one-line description.</param>
    /// <param name="multiSelect">False shows radio buttons (one pick); true shows checkboxes (several).</param>
    /// <param name="allowOther">Whether an "Other, namely…" row with a text field is offered.</param>
    /// <param name="header">A short chip shown above the question. Null for none.</param>
    Task<AskStructuredQuestionResult> AskStructuredQuestionAsync(
        string question,
        IReadOnlyList<(string Label, string? Description)> options,
        bool multiSelect,
        bool allowOther,
        string? header,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens <paramref name="url"/> in the operator's default browser (AC-587) — this door exists only because
    /// <c>Cockpit.Infrastructure</c>, where the <c>open_url</c> tool lives, cannot reference <c>Cockpit.App</c> and
    /// so cannot reach <c>ExternalLink</c> itself. Refuses outright when <paramref name="url"/> is not an absolute
    /// <c>http</c>/<c>https</c> address — the same rule <c>ExternalLink.TryParseWebAddress</c> enforces elsewhere,
    /// applied here rather than re-decided. Never reaches <c>TryOpenWithSystemApp</c>, which opens a filesystem path rather than a page.
    /// </summary>
    Task<OpenUrlResult> OpenUrlAsync(string url, CancellationToken cancellationToken = default);
}

// What came of binding a shared project (AC-798). Same shape and same reason as `AgentStopResult` — a refusal is a
// sentence the assistant says, not an exception it fails on.
//
// `SourceName`: the connection it came from ("Depot — Work"), so the confirmation names where the project is from
// rather than only what it is now called.
// `SourceDirectory`: the folder it was pointed at — echoed back because it is the one field on the consent card, and
// the one thing the operator can still check afterwards.
public sealed record AssistantProjectBindResult(
    bool Ok, string? ProjectId, string? Name, string? SourceName, string? SourceDirectory, string? Error)
{
    public static AssistantProjectBindResult Bound(string projectId, string name, string sourceName, string? sourceDirectory) =>
        new(true, projectId, name, sourceName, sourceDirectory, null);

    public static AssistantProjectBindResult Refused(string error) => new(false, null, null, null, null, error);
}

// What came of creating a project (AC-799). Same shape and same reason as `AgentStopResult` — a refusal is a
// sentence the assistant says, not an exception it fails on.
public sealed record AssistantProjectCreateResult(bool Ok, string? ProjectId, string? Name, string? Error)
{
    public static AssistantProjectCreateResult Created(string projectId, string name) => new(true, projectId, name, null);

    public static AssistantProjectCreateResult Refused(string error) => new(false, null, null, error);
}

// What came of raising a structured question card (AC-955). Same shape and same reason as `AgentStopResult` — a
// refusal is a sentence the assistant says, not an exception it fails on.
public sealed record AskStructuredQuestionResult(bool Ok, string? Error)
{
    public static AskStructuredQuestionResult Shown() => new(true, null);

    public static AskStructuredQuestionResult Refused(string error) => new(false, error);
}

// What came of a handover. Same shape and same reason as `AgentStopResult` — a refusal is a sentence the
// assistant says, not an exception it fails on.
public sealed record WorktreeHandoverResult(bool Ok, string? Path, string? Branch, string? SessionName, string? Error)
{
    public static WorktreeHandoverResult HandedOver(string path, string branch, string sessionName) =>
        new(true, path, branch, sessionName, null);

    public static WorktreeHandoverResult Refused(string error) => new(false, null, null, null, error);
}

// What came of opening a web address (AC-587). Same shape and same reason as `AgentStopResult` — a refusal is
// a sentence the assistant says, not an exception it fails on.
//
// `Url`: the address that was opened, echoed back so the assistant can say which one it was rather than
// assuming its own argument survived unchanged.
public sealed record OpenUrlResult(bool Ok, string? Url, string? Error)
{
    public static OpenUrlResult Opened(string url) => new(true, url, null);

    public static OpenUrlResult Refused(string error) => new(false, null, error);
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
// The profile to run under, by its label. Required unless `ProjectId` names a project with its own
// `Project.DefaultProfileLabel` (AC-773) — omitted then, that default is used instead, and the label actually used
// comes back as `AgentSpawnResult.ResolvedProfileLabel` so the assistant can say which one it was, never silently.
// Naming one here always wins over the project's default (AC-436 guardrail 6 still holds: an agent that did not have
// to say so would spawn Opus workers by accident, so an explicit label is never overruled by a project).
// `ProjectId` (AC-773):
// The project this session works on, by its id from `list_projects` — the one thing `CockpitViewModel
// .StartSessionOnWorkspaceAsync` needs to apply that project's working directory, profile default, worktree
// isolation, behaviour prompt, memory/resources and MCP overlay in one pass, the same way it already does for a
// folder that happens to map-match a project (AC-682). Left out, resolution falls back to that map-match exactly as
// before. An id that names no project is refused rather than silently falling back to the folder guess.
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
    string? ProfileLabel,
    string? ProjectId = null,
    string? Prompt = null,
    string? WorkingDirectory = null,
    string? SessionName = null,
    string? Kind = null,
    IReadOnlyDictionary<string, string>? OptionOverrides = null,
    bool? IsolateInWorktree = null);

// What came of a spawn. A refusal carries `Error` and no pane; both are reported to the agent, so a
// spawn that could not happen is a sentence the operator hears rather than a session that silently is not there.
//
// `PromptDelivered` (AC-760): null when no `Prompt` was given; true when it was submitted on the spot; false when
// the pane exists but the CLI was not yet reading stdin, so it is held rather than lost or silently claimed sent.
//
// `ResolvedProfileLabel` (AC-773): the profile actually used — the request's own `ProfileLabel` echoed back, or,
// when that was left out, whatever the resolved project's `DefaultProfileLabel` supplied. Reported so the assistant
// says which one it was rather than assuming, the same reason a profile was made required in the first place.
public sealed record AgentSpawnResult(
    bool Ok, string? PaneId, string? SessionName, string? WorkingDirectory, string? Error,
    bool? PromptDelivered = null, string? ResolvedProfileLabel = null)
{
    public static AgentSpawnResult Started(
        string paneId, string sessionName, string? workingDirectory, bool? promptDelivered = null, string? resolvedProfileLabel = null) =>
        new(true, paneId, sessionName, workingDirectory, null, promptDelivered, resolvedProfileLabel);

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
