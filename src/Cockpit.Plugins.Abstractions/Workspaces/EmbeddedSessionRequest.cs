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
}
