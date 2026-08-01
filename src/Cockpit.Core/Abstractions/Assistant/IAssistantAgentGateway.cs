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
}

/// <summary>What came of removing a workspace. Same shape and same reason as <see cref="AgentStopResult"/>.</summary>
/// <param name="Name">The tab label the desk had, so the confirmation names what the operator will see disappear.</param>
public sealed record WorkspaceRemovalResult(bool Ok, string? Name, string? Error)
{
    public static WorkspaceRemovalResult Removed(string name) => new(true, name, null);

    public static WorkspaceRemovalResult Refused(string error) => new(false, null, error);
}

/// <summary>One session to start: where it goes, what it runs, and what it is handed to begin with.</summary>
/// <param name="Target">The desk and the rule that chose it. See <see cref="SpawnTarget"/>.</param>
/// <param name="ProfileLabel">
/// The profile to run under, by its label. Required and never defaulted: which profile a session runs under decides
/// its provider, its model and therefore its cost, and an agent that did not have to say so would spawn Opus workers
/// by accident (AC-436 guardrail 6, which holds for whatever the assistant starts too).
/// </param>
/// <param name="Prompt">The first message to hand the session once it is up, or null to leave it waiting.</param>
/// <param name="WorkingDirectory">The folder to run in, or null for the profile's own default.</param>
/// <param name="SessionName">What to call the pane, or null to let the profile and the clock name it.</param>
/// <param name="Kind">
/// The route to start on — "sdk" or "tty" — or null for the one the profile is set to, which is what nearly every
/// spawn should use.
/// </param>
/// <remarks>
/// <b>Why the route is here at all.</b> The New-session dialog has a Kind toggle, so "the same profile, but as an
/// SDK session" is an ordinary thing to want. Without a way to say it here, that request has nowhere to land — and
/// what an assistant does with a request it has no tool for is reach for the nearest thing that sounds close. Asked
/// for exactly this, it went looking through <c>cockpit-orchestrator</c>, which starts work with no pane, outside
/// this ticket's consent gate and outside its trail. A missing parameter turned into a detour around the guardrail.
/// </remarks>
public sealed record AgentSpawnRequest(
    SpawnTarget Target,
    string ProfileLabel,
    string? Prompt = null,
    string? WorkingDirectory = null,
    string? SessionName = null,
    string? Kind = null);

/// <summary>
/// What came of a spawn. A refusal carries <see cref="Error"/> and no pane; both are reported to the agent, so a
/// spawn that could not happen is a sentence the operator hears rather than a session that silently is not there.
/// </summary>
public sealed record AgentSpawnResult(bool Ok, string? PaneId, string? SessionName, string? WorkingDirectory, string? Error)
{
    public static AgentSpawnResult Started(string paneId, string sessionName, string? workingDirectory) =>
        new(true, paneId, sessionName, workingDirectory, null);

    public static AgentSpawnResult Refused(string error) => new(false, null, null, null, error);
}

/// <summary>What came of a stop. Same shape and same reason as <see cref="AgentSpawnResult"/>.</summary>
public sealed record AgentStopResult(bool Ok, string? PaneId, string? SessionName, string? Error)
{
    public static AgentStopResult Stopped(string paneId, string sessionName) => new(true, paneId, sessionName, null);

    public static AgentStopResult Refused(string error) => new(false, null, null, error);
}

/// <summary>
/// One desk, as the assistant may see it: enough to name it back to the operator and to spawn onto it.
/// </summary>
/// <param name="Id">The workspace id — what a spawn actually takes.</param>
/// <param name="Name">The tab label the operator sees, which is the name they will say out loud.</param>
/// <param name="Type">The workspace type id ("sessions", "dashboard", "projects", a plugin's own).</param>
/// <param name="CanHostSessions">
/// Whether a session may be placed here at all. Only a Sessions desk can show one — a dashboard would run it
/// invisibly, which is worse than refusing — so this is reported rather than left for the assistant to infer from
/// <paramref name="Type"/>.
/// </param>
/// <param name="SessionCount">How many agent sessions are on it right now.</param>
/// <param name="IsActive">Whether this is the desk the operator is looking at.</param>
public sealed record AssistantWorkspaceRow(
    string Id,
    string Name,
    string Type,
    bool CanHostSessions,
    int SessionCount,
    bool IsActive);

/// <summary>
/// One profile a spawn may name.
/// </summary>
/// <param name="Label">Exactly what <c>start_agent</c> takes — the label, not the display string.</param>
/// <param name="Provider">Which provider it runs on ("Claude", "LM Studio"), so "a Claude one" can be resolved without guessing from the label's wording.</param>
/// <param name="Model">The model, where the profile pins one. Null means the provider's own default, which is worth saying rather than showing as blank.</param>
public sealed record AssistantProfileRow(string Label, string Provider, string? Model);
