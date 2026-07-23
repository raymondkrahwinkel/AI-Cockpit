namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// What to start when a workspace embeds a session (<see cref="IWorkspaceContext.EmbedSession"/>). Thin on
/// purpose — the same handful of things a placed session pane persists — so a plugin says which identity runs
/// where and lets the host apply everything else the way a normal session start does.
/// </summary>
public sealed record EmbeddedSessionRequest
{
    /// <summary>The profile the session runs under (its provider and identity); null starts the cockpit's default profile.</summary>
    public string? ProfileId { get; init; }

    /// <summary>The directory the session starts in; null uses the app's own working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// When true and <see cref="WorkingDirectory"/> is a git repository, the host creates a fresh worktree on its own
    /// branch for this session (AC-85) and runs it there instead of in the folder as given — the same isolation the
    /// New-session dialog offers, so an embedded run (Autopilot) does not edit the operator's real checkout. A
    /// non-repository directory, or a host without a worktree manager, runs in the folder as given.
    /// </summary>
    public bool IsolateInWorktree { get; init; }

    /// <summary>
    /// An existing worktree directory to run this session in (AC-174, Raymond 2026-07-22) — used with
    /// <see cref="IsolateInWorktree"/> to run several sessions in <em>one</em> shared worktree rather than each creating
    /// its own. When set, the host runs the session in this directory and does <em>not</em> create a new worktree, but
    /// the isolation gate still applies (a non-confining provider is still refused). This is how an Autopilot run gives
    /// every step the same worktree so their work accumulates on one branch, instead of a fresh throwaway worktree per
    /// step. Null (the default) keeps the create-a-fresh-worktree behaviour of <see cref="IsolateInWorktree"/>.
    /// </summary>
    public string? WorktreePath { get; init; }

    /// <summary>
    /// Ask the driver to confine this session's file tools to its <see cref="WorkingDirectory"/> (AC-174, Raymond
    /// 2026-07-22) even when the session is not itself creating a worktree. <see cref="IsolateInWorktree"/> already
    /// implies this; set it explicitly for a session that runs <em>in</em> a run's worktree without isolating (the
    /// Autopilot CEO validator, which reads the accumulated work there) so that — if it runs on a local model — its file
    /// servers are re-rooted at the worktree and every escape channel is dropped, rather than reaching the operator's
    /// home. A provider that confines natively ignores it. Only set this when <see cref="WorkingDirectory"/> is a
    /// worktree, never the real checkout.
    /// </summary>
    public bool ConfineFileToolsToWorkingDirectory { get; init; }

    /// <summary>
    /// The permission mode the session starts in (e.g. <c>acceptEdits</c>, <c>bypassPermissions</c>) — how autonomous
    /// it is on the CLI side (AC-152). Null starts on the app default ("ask"). The host's ConsentBroker still gates
    /// shell, egress and other sensitive actions regardless of this, so a more autonomous mode is not an ungated one.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// The model to run on, where the profile's provider offers a choice — Claude and Codex expose a model; a local
    /// profile pins its own, so this is null there. Null uses the profile's own default model. AC-174: a CEO plan can
    /// pick a model per step, so each step's embedded session starts on the model the operator approved for it.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The minimal set of MCP server ids to launch this session with — only what the step needs, not everything
    /// (AC-174, Raymond 2026-07-21): a smaller MCP surface is fewer tool definitions in the agent's context (tokens)
    /// and tighter least-privilege (AC-117). Empty keeps the host's usual selection for the profile; a non-empty list
    /// restricts the session to exactly those servers. Ids as the host advertises them (e.g. <c>cockpit-verify</c>).
    /// </summary>
    public IReadOnlyList<string> McpServers { get; init; } = [];

    /// <summary>
    /// A hidden system prompt prepended for this one session (AC-180) — the role and working instructions an embedded
    /// run hands its agent at start (Autopilot's "you are the CEO, this is how you plan, emit the plan through the
    /// tool") that the operator never sees as a turn. Given at start, so it carries no risk of racing the session's
    /// runtime the way a post-start message does. Provider-agnostic: the host passes it to whichever driver runs the
    /// session and the driver applies it its own way (a CLI's <c>--append-system-prompt</c>, a leading system message
    /// for a local model); a provider that cannot inject one ignores it. Null or blank adds nothing.
    /// </summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>
    /// A first user turn to submit automatically once the session is up (AC-174) — how an autonomous embedded run (an
    /// Autopilot step agent) is set going without a human typing: its task brief goes here as the opening message. The
    /// host submits it <em>after</em> the runtime has started, so it cannot race the "session has not started yet" gate
    /// a message sent right after <see cref="IWorkspaceContext.EmbedSession"/> would hit. Unlike
    /// <see cref="AppendSystemPrompt"/> this is a visible turn — it is the agent's opening instruction, not a hidden
    /// role. Null or blank starts the session idle, waiting for the operator (the CEO planning round works this way).
    /// </summary>
    public string? InitialUserMessage { get; init; }

    /// <summary>
    /// Starts the session with its composer disabled (AC-174) — an autonomous embedded run (an Autopilot step agent)
    /// drives itself, so the operator should not think they can type into it; the input box is off until they
    /// deliberately re-enable it through <see cref="IEmbeddedSession.SetInputEnabled"/> (the surface's "intervene"
    /// affordance). Only the composer is disabled — the host still submits the run's own <see cref="InitialUserMessage"/>
    /// and drives the session as usual. Defaults to <see langword="false"/>: an ordinary embedded session (the CEO
    /// planning round) is a conversation and keeps its composer live.
    /// </summary>
    public bool StartWithInputDisabled { get; init; }

    /// <summary>
    /// Tool names this embedded session may run without raising a mid-run permission prompt (AC-215) — the plugin's own
    /// control tools for an autonomous run (Autopilot's <c>autopilot_step_done</c>, <c>autopilot_blocked</c>,
    /// <c>autopilot_validate</c>), authorized up front so a self-driving run does not stop halfway to ask the operator to
    /// allow a tool the run itself depends on. These are the plugin's <em>own</em> in-process endpoint tools, never file,
    /// shell or egress tools — those stay gated by the permission mode and the host's ConsentBroker regardless. Names as
    /// the agent sees them (e.g. <c>mcp__cockpit-autopilot-run__autopilot_step_done</c>). Empty (the default) pre-approves
    /// nothing, so an ordinary embedded session keeps prompting as before, and a host that does not honour this keeps
    /// compiling untouched.
    /// </summary>
    public IReadOnlyList<string> PreApprovedTools { get; init; } = [];

    /// <summary>
    /// Whether this session auto-allows <em>every</em> tool call without a prompt (AC-215, Raymond 2026-07-23) — the
    /// "worktree is the boundary" stance for an autonomous run isolated in a throwaway git worktree: it needs to run
    /// its work tools (Bash, edits, git) with no one to answer a prompt, so the run's isolation is the containment
    /// rather than the per-call gate. Deliberately broad — it includes shell and egress, so the operator accepts that a
    /// run can reach outside its worktree (prompt-injection from an untrusted issue), bounded to the run. Only an
    /// autonomous, isolated embedded run (Autopilot's steps and its validating CEO) sets this; an ordinary session
    /// leaves it false and keeps prompting. Supersedes <see cref="PreApprovedTools"/> when true.
    /// </summary>
    public bool PreApproveAllTools { get; init; }
}
