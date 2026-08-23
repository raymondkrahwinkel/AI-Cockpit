namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// What to start when a workspace embeds a session (<see cref="IWorkspaceContext.EmbedSession"/>). Thin on
/// purpose — the same handful of things a placed session pane persists — so a plugin says which identity runs
/// where and lets the host apply everything else the way a normal session start does.
/// </summary>
public sealed record EmbeddedSessionRequest
{
    /// <summary>
    /// The profile the session runs under (its provider and identity); null starts the cockpit's default profile.
    /// </summary>
    public string? ProfileId { get; init; }

    /// <summary>
    /// The directory the session starts in; null uses the app's own working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// When true and <see cref="WorkingDirectory"/> is a git repository, the host creates a fresh worktree on its
    /// own branch for this session (AC-85) and runs it there. A non-repository directory, or a host without a
    /// worktree manager, runs in the folder as given.
    /// </summary>
    public bool IsolateInWorktree { get; init; }

    /// <summary>
    /// An existing worktree directory to run this session in (AC-174) — used with <see cref="IsolateInWorktree"/>
    /// to run several sessions in <em>one</em> shared worktree rather than each creating its own.
    /// </summary>
    /// <remarks>
    /// When set, the host runs the session here and does <em>not</em> create a new worktree, but the isolation
    /// gate still applies. Null (the default) keeps the create-a-fresh-worktree behaviour of
    /// <see cref="IsolateInWorktree"/>.
    /// </remarks>
    public string? WorktreePath { get; init; }

    /// <summary>
    /// Ask the driver to confine this session's file tools to its <see cref="WorkingDirectory"/> (AC-174), even
    /// when the session is not itself creating a worktree.
    /// </summary>
    /// <remarks>
    /// <see cref="IsolateInWorktree"/> already implies this; set it explicitly for a session that runs in a run's
    /// worktree without isolating. A provider that confines natively ignores it. Only set this when
    /// <see cref="WorkingDirectory"/> is a worktree, never the real checkout.
    /// </remarks>
    public bool ConfineFileToolsToWorkingDirectory { get; init; }

    /// <summary>
    /// The permission mode the session starts in (e.g. <c>acceptEdits</c>, <c>bypassPermissions</c>) — how
    /// autonomous it is on the CLI side (AC-152). Null starts on the app default ("ask").
    /// </summary>
    /// <remarks>
    /// The host's ConsentBroker still gates shell, egress and other sensitive actions regardless of this.
    /// </remarks>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// The model to run on, where the profile's provider offers a choice; null there for a profile that pins its
    /// own. Null uses the profile's own default model.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The minimal set of MCP server ids to launch this session with — only what the step needs, not everything
    /// (AC-174): a smaller surface is fewer tool definitions in context and tighter least-privilege.
    /// </summary>
    /// <remarks>
    /// Empty keeps the host's usual selection for the profile; a non-empty list restricts the session to exactly
    /// those servers.
    /// </remarks>
    public IReadOnlyList<string> McpServers { get; init; } = [];

    /// <summary>
    /// A hidden system prompt prepended for this one session (AC-180) — role and working instructions an embedded
    /// run hands its agent at start, that the operator never sees as a turn.
    /// </summary>
    /// <remarks>
    /// Given at start, so it cannot race the session's runtime. Provider-agnostic: the driver applies it its own
    /// way; a provider that cannot inject one ignores it. Null or blank adds nothing.
    /// </remarks>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>
    /// A first user turn to submit automatically once the session is up (AC-174) — how an autonomous embedded run
    /// is set going without a human typing.
    /// </summary>
    /// <remarks>
    /// The host submits it <em>after</em> the runtime has started, so it cannot race the "session has not started
    /// yet" gate. Unlike <see cref="AppendSystemPrompt"/> this is a visible turn. Null or blank starts the session
    /// idle, waiting for the operator.
    /// </remarks>
    public string? InitialUserMessage { get; init; }

    /// <summary>
    /// Starts the session with its composer disabled (AC-174), for an autonomous embedded run that drives itself
    /// — the input box is off until re-enabled through <see cref="IEmbeddedSession.SetInputEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Only the composer is disabled — the host still submits <see cref="InitialUserMessage"/> and drives the
    /// session as usual. Defaults to <see langword="false"/>.
    /// </remarks>
    public bool StartWithInputDisabled { get; init; }

    /// <summary>
    /// Tool names this embedded session may run without raising a mid-run permission prompt (AC-215) — the
    /// plugin's own control tools for an autonomous run, authorized up front.
    /// </summary>
    /// <remarks>
    /// These are the plugin's own in-process endpoint tools, never file, shell or egress tools — those stay gated
    /// regardless. Names as the agent sees them. Empty (the default) pre-approves nothing.
    /// </remarks>
    public IReadOnlyList<string> PreApprovedTools { get; init; } = [];

    /// <summary>
    /// Whether this session auto-allows <em>every</em> tool call without a prompt (AC-215) — the "worktree is the
    /// boundary" stance for an autonomous run isolated in a throwaway git worktree.
    /// </summary>
    /// <remarks>
    /// Deliberately broad — includes shell and egress. Only an autonomous, isolated embedded run sets this; an
    /// ordinary session leaves it false. Supersedes <see cref="PreApprovedTools"/> when true.
    /// </remarks>
    public bool PreApproveAllTools { get; init; }

    /// <summary>
    /// The run this session is being embedded for (AC-251) — the plugin's own identifier for one piece of work,
    /// the same value on every session that run embeds.
    /// </summary>
    /// <remarks>
    /// The host records it against the session's token and cost totals, so "what did that run spend" is a
    /// grouping. Not interpreted by the host — any value stable for the run's lifetime will do. Null (the
    /// default) records the session as belonging to no run.
    /// </remarks>
    public string? RunId { get; init; }

    /// <summary>
    /// A human name for <see cref="RunId"/> — what the run is called or working on — recorded alongside it so a
    /// costly run can be recognised in the usage trail without resolving the id against the plugin that issued it.
    /// Null adds nothing; it is a convenience for reading the trail, never an identifier.
    /// </summary>
    public string? RunLabel { get; init; }
}
