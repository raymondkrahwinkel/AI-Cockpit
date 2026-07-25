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
    /// AC-174). The result is a short reason when the host ended the session <em>itself</em> (why it refused to
    /// isolate, or that it failed to start), so an embedder can show it; null for an ordinary end (torn down after its
    /// work, or the workspace closed). An embedder that waits on the session doing something — Autopilot awaiting a
    /// step agent's done-report — awaits this alongside it, so a session that dies before it ever reports is a finished
    /// wait it can act on (and explain), not a hang. Never faults; it simply completes.
    /// </summary>
    Task<string?> Completion { get; }

    /// <summary>
    /// Whether this embedded session is mid-turn (AC-195): true from the moment a turn is sent until it settles,
    /// mirroring the session's own busy state. An embedder that runs a session the operator watches — the Autopilot
    /// plan pop-out's CEO, whose planning turn can run silently for minutes — shows a "working" cue while this is true
    /// so a long turn does not read as a hang. Default <see langword="false"/> for a host or adapter that does not
    /// surface it, so an implementation from before this signal keeps compiling and simply reports "not busy".
    /// </summary>
    bool IsBusy => false;

    /// <summary>
    /// Raised when <see cref="IsBusy"/> flips, carrying the new value, so an embedder can light or clear its "working"
    /// cue as the turn starts and settles without polling. Marshalled to the UI thread by the host. Default no-op for
    /// an adapter that does not surface a busy signal.
    /// </summary>
    event Action<bool>? BusyChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when this session makes real tool progress — a tool call surfacing or a tool result landing. An embedder
    /// that fails a silent step on a stall deadline (Autopilot's per-step timeout) resets that deadline on this, so a
    /// step that is slow because it is working hard is not failed as stuck — only a genuinely no-progress agent (AC-192:
    /// a turn that emits text describing a tool it never runs) hits the deadline. Deliberately not raised on text or
    /// thinking, which a stuck agent still produces. Marshalled to the UI thread by the host. Default no-op for an
    /// adapter that does not surface it, so an implementation from before this signal keeps compiling.
    /// </summary>
    event Action? Activity
    {
        add { }
        remove { }
    }
}
