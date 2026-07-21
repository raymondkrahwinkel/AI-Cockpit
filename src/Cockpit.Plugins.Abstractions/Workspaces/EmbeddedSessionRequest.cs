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
}
