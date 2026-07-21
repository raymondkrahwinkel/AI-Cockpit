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
}
