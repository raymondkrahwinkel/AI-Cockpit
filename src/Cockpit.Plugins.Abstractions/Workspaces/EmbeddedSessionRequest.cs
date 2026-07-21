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
}
