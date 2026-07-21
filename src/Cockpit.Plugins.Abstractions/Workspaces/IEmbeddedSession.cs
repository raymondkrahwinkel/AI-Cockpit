using Avalonia.Controls;

namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// A live cockpit session embedded in a plugin workspace (<see cref="IWorkspaceContext.EmbedSession"/>): the
/// control to place in the body, and the pane id to act on it. The host owns the session — it built it, it ends
/// it when the workspace closes — so there is nothing here to dispose; the plugin holds the place, not the
/// lifetime.
/// </summary>
public interface IEmbeddedSession
{
    /// <summary>The session's live view, ready to drop into the body's layout. The host keeps it alive across re-layouts.</summary>
    Control View { get; }

    /// <summary>
    /// The embedded session's <c>IPluginSessionContext.PaneId</c> — the handle to act on this exact session
    /// (set its statusline, send it an intent, name it) through <see cref="ICockpitHost"/>.
    /// </summary>
    string PaneId { get; }

    /// <summary>
    /// Ends this one embedded session now — tears down its runtime and releases its worktree — without waiting for
    /// the workspace to close. What a body calls when it replaces one run's session with another's on the same
    /// surface, so the previous run's session and worktree are not left orphaned. Closing the workspace still ends
    /// any session left embedded; this is the finer-grained handle.
    /// </summary>
    Task CloseAsync();

    /// <summary>
    /// Enables or disables this session's composer (AC-174). A session started with its input disabled
    /// (<see cref="EmbeddedSessionRequest.StartWithInputDisabled"/>) runs autonomously; the surface's "intervene"
    /// affordance calls this with <see langword="true"/> to hand the operator the keyboard, and could disable it
    /// again. Affects only whether the operator can type — the host still drives the session (its opening brief, its
    /// turns) regardless. Marshalled to the UI thread by the host, so it is safe to call from anywhere.
    /// </summary>
    void SetInputEnabled(bool enabled);

    /// <summary>
    /// Completes when this embedded session ends — its runtime torn down and worktree released — whatever the cause:
    /// the workspace closing, an explicit <see cref="CloseAsync"/>, the session self-closing, or the host refusing to
    /// run it at all (an isolate-in-worktree run on a provider that cannot confine file access to the worktree,
    /// AC-174). An embedder that waits on the session doing something — Autopilot awaiting a step agent's done-report
    /// — awaits this alongside it, so a session that dies before it ever reports is a finished wait it can act on,
    /// not a hang. Never faults; it simply completes.
    /// </summary>
    Task Completion { get; }
}
