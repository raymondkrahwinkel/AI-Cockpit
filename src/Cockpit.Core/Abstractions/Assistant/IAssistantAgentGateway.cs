namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The host-side spawn service (AC-545): the one place a session is started, stopped or a desk created on an agent's
/// behalf. Every method takes a <see cref="SpawnTarget"/>, buildable only through its two named doors, never deriving/
/// defaulting a workspace, and never throws for a tellable outcome — comes back as a result with a reason instead.
/// </summary>
public interface IAssistantAgentGateway
{
    /// <summary>
    /// Starts a session on <paramref name="request"/>'s profile and places it on the target's desk as an ordinary
    /// pane, with its own consents and transcript. The desk is <em>not</em> activated: a spawn elsewhere must leave
    /// the operator where they are, since work is often set up on another desk precisely so it does not interrupt.
    /// </summary>
    Task<AgentSpawnResult> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the session on <paramref name="paneId"/>, whichever desk it is on. Refuses a pane that is not an agent
    /// session, one that does not exist, and the assistant's own. <paramref name="caller"/> defaults to the assistant
    /// (only caller until AC-795 gave a paired controller its own tools) — logging a stop from elsewhere as the assistant reads plausibly and is wrong.
    /// </summary>
    Task<AgentStopResult> StopAsync(
        string paneId,
        SpawnCaller caller = SpawnCaller.Assistant,
        string? callerPaneId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames the session on <paramref name="paneId"/> — the title in its header and sidebar row — exactly as an
    /// inline rename does, so it counts as a name somebody chose rather than one a later suggestion may replace.
    /// Refuses what <see cref="StopAsync"/> refuses: a pane not there, one inside a workspace's own surface, the assistant's own.
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
    /// The profiles a spawn may name, with the provider and model each runs on. A spawn's profile is required and
    /// never defaulted, so without this the assistant's only way to learn the labels was guessing and reading the
    /// refusal. Asking "which profile?" is right; reciting all of them when the operator already said "a Claude one" is not.
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
    /// unlike a message. Refuses what <see cref="StopAsync"/> refuses. A just-started session holds the turn
    /// instead of dropping it — <see cref="AgentPromptResult.Delivered"/> says which happened, never true for a wait.
    /// </summary>
    Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Arms the host-side session watcher (AC-640) on <paramref name="paneId"/> for one or more of the five event kinds,
    /// so the assistant is told when a session finishes, stalls, disappears or prints something it asked about, instead
    /// of polling <c>list_sessions</c>. Per pane, not automatic like <c>CiWatcher</c>. Refuses a dead pane, <c>stuck</c>/<c>pattern</c> with no transcript, or an invalid regex.
    /// </summary>
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
    /// the same route the "Add to my projects…" dialog runs, never a direct write to the project store. Three things
    /// never default — local folder, profile, one local reference per resource row — refused with the question when missing. Does not clone; the folder must exist.
    /// </summary>
    Task<AssistantProjectBindResult> BindSharedProjectAsync(
        string sharedProjectId,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a brand-new local project (AC-799), not bound to any shared definition unlike <see cref="BindSharedProjectAsync"/>.
    /// A name matching one a connection already shares is refused (use that door instead). Null leaves each optional field
    /// to its default (no folder/profile/prompt/category, MCP servers all ticked, no worktree isolation); <paramref name="sourceDirectory"/> must exist when given.
    /// </summary>
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
    /// Puts a clarifying question to the operator as a card in the assistant's own chat window (AC-955): radio
    /// buttons (<paramref name="multiSelect"/> false) or checkboxes, plus an optional "Other, namely…" row. Returns
    /// as soon as shown — the card sits until the UI's <c>SubmitQuestionAnswersCommand</c> resolves it, which this cannot await.
    /// </summary>
    Task<AskStructuredQuestionResult> AskStructuredQuestionAsync(
        string question,
        IReadOnlyList<(string Label, string? Description)> options,
        bool multiSelect,
        bool allowOther,
        string? header,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a fresh assistant conversation for after its current turn finishes.
    /// </summary>
    Task<ClearConversationResult> RequestConversationClearAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ClearConversationResult.Refused("This gateway cannot queue a conversation clear."));

    /// <summary>
    /// Opens <paramref name="url"/> in the operator's default browser (AC-587) — exists only because
    /// <c>Cockpit.Infrastructure</c>, where <c>open_url</c> lives, cannot reference <c>Cockpit.App</c> to reach
    /// <c>ExternalLink</c>. Refuses a non-absolute <c>http</c>/<c>https</c> address, same rule as <c>ExternalLink.TryParseWebAddress</c>.
    /// </summary>
    Task<OpenUrlResult> OpenUrlAsync(string url, CancellationToken cancellationToken = default);
}

// AC-1013: What came of binding a shared project (AC-798) — a refusal is a sentence the assistant says, not an
// exception it fails on. SourceName/SourceDirectory are echoed back so the confirmation names where it came from.
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

// A repeated request is still successful: the requested end state is already pending.
public sealed record ClearConversationResult(bool Ok, bool AlreadyPending, string? Error)
{
    public static ClearConversationResult Queued(bool alreadyPending) => new(true, alreadyPending, null);

    public static ClearConversationResult Refused(string error) => new(false, false, error);
}

// What came of a handover. Same shape and same reason as `AgentStopResult` — a refusal is a sentence the
// assistant says, not an exception it fails on.
public sealed record WorktreeHandoverResult(bool Ok, string? Path, string? Branch, string? SessionName, string? Error)
{
    public static WorktreeHandoverResult HandedOver(string path, string branch, string sessionName) =>
        new(true, path, branch, sessionName, null);

    public static WorktreeHandoverResult Refused(string error) => new(false, null, null, null, error);
}

// AC-1013: What came of opening a web address (AC-587). Url is echoed back so the assistant can say which one
// it was rather than assuming its own argument survived unchanged.
public sealed record OpenUrlResult(bool Ok, string? Url, string? Error)
{
    public static OpenUrlResult Opened(string url) => new(true, url, null);

    public static OpenUrlResult Refused(string error) => new(false, null, error);
}

// AC-1013: What came of arming a watch. Name is the watched session's title, so the confirmation names what is
// being watched rather than a pane id nobody can check by ear.
public sealed record AssistantWatchResult(bool Ok, string? Name, string? Error)
{
    public static AssistantWatchResult Watched(string name) => new(true, name, null);

    public static AssistantWatchResult Refused(string error) => new(false, null, error);
}

// AC-1013: What came of a message. DeliversAtTurnStart is reported because "delivered" on a pane with no
// passive delivery only means waiting, not told — else the assistant could wrongly report "I told them".
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

// AC-1013: What came of a prompt. Delivered false means the session isn't up yet and the turn is held, not an
// error — without this field an assistant could wrongly say "sent" for work that hasn't started.
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

// AC-1013: One session to start — desk, profile (overrides project default, AC-436 guardrail 6), and what it's
// handed (Prompt, OptionOverrides AC-648/649, IsolateInWorktree AC-719). Kind exists because a Kind-less request
// once got mis-routed through cockpit-orchestrator, bypassing this gate — full history belongs on AC-1013.
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

// AC-1013: What came of a spawn — a refusal carries Error and no pane, so a failed spawn is a sentence the
// operator hears, not a session that's silently missing. PromptDelivered (AC-760) and ResolvedProfileLabel
// (AC-773) are reported so the assistant states what actually happened rather than assuming.
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

// AC-1013: One desk, as the assistant may see it. CanHostSessions is reported explicitly, not inferred from
// Type, because a dashboard desk would run a spawned session invisibly rather than refusing it.
public sealed record AssistantWorkspaceRow(
    string Id,
    string Name,
    string Type,
    bool CanHostSessions,
    int SessionCount,
    bool IsActive);

// AC-1013: One profile a spawn may name. Provider is reported so "a Claude one" resolves without guessing from
// the label's wording; a null Model means the provider's own default, distinct from blank.
public sealed record AssistantProfileRow(string Label, string Provider, string? Model)
{
    // What this profile is actually configured to run at, in its provider's own vocabulary (AC-647) — read from
    // the provider's declared option schema, so a raw settings dump never has to be guessed at. Empty for a
    // provider that declares nothing; that is an answer, not a gap to fill with another provider's fields.
    public IReadOnlyList<AssistantProfileOptionRow> Options { get; init; } = [];
}

// AC-1013: One option a profile's provider understands (AC-647). SetOnProfile distinguishes what the profile
// chose from what merely applies as the provider's own default.
public sealed record AssistantProfileOptionRow(
    string Key,
    string Label,
    string? Value,
    string? ValueLabel,
    bool SetOnProfile);
