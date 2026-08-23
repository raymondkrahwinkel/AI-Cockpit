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
    /// <summary>
    /// The session's live view, ready to drop into the body's layout. The host keeps it alive across re-layouts.
    /// </summary>
    Control View { get; }

    /// <summary>
    /// The embedded session's <c>IPluginSessionContext.PaneId</c> — the handle to act on this exact session
    /// (set its statusline, send it an intent, name it) through <see cref="ICockpitHost"/>.
    /// </summary>
    string PaneId { get; }

    /// <summary>
    /// The directory this session actually runs in — its isolated worktree when the host made one, the working
    /// directory otherwise (AC-1037). Null until the session has started, and for an adapter that does not surface it.
    /// </summary>
    string? WorktreePath => null;

    /// <summary>
    /// Ends this one embedded session now — tears down its runtime and releases its worktree — without waiting
    /// for the workspace to close.
    /// </summary>
    /// <remarks>
    /// What a body calls when it replaces one run's session with another's on the same surface. Closing the
    /// workspace still ends any session left embedded; this is the finer-grained handle.
    /// </remarks>
    Task CloseAsync();

    /// <summary>
    /// Enables or disables this session's composer (AC-174). Affects only whether the operator can type — the
    /// host still drives the session regardless.
    /// </summary>
    /// <remarks>
    /// A session started with input disabled runs autonomously; the "intervene" affordance calls this with
    /// <see langword="true"/> to hand the operator the keyboard. Marshalled to the UI thread by the host.
    /// </remarks>
    void SetInputEnabled(bool enabled);

    /// <summary>
    /// Completes when this embedded session ends — its runtime torn down and worktree released — whatever the
    /// cause. Never faults; it simply completes.
    /// </summary>
    /// <remarks>
    /// The result is a short reason when the host ended the session itself; null for an ordinary end. An embedder
    /// awaiting the session doing something can await this alongside it, so a session that dies before it ever
    /// reports is a finished wait it can act on, not a hang.
    /// </remarks>
    Task<string?> Completion { get; }

    /// <summary>
    /// Whether this embedded session is mid-turn (AC-195): true from the moment a turn is sent until it settles.
    /// </summary>
    /// <remarks>
    /// An embedder that runs a session the operator watches shows a "working" cue while this is true. Default
    /// <see langword="false"/> for an adapter that does not surface it.
    /// </remarks>
    bool IsBusy => false;

    /// <summary>
    /// Raised when <see cref="IsBusy"/> flips, carrying the new value, so an embedder can light or clear its
    /// "working" cue without polling.
    /// </summary>
    /// <remarks>
    /// Marshalled to the UI thread by the host. Default no-op for an adapter that does not surface a busy signal.
    /// </remarks>
    event Action<bool>? BusyChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when this session makes real tool progress — a tool call surfacing or a tool result landing.
    /// Deliberately not raised on text or thinking, which a stuck agent still produces.
    /// </summary>
    /// <remarks>
    /// An embedder that fails a silent step on a stall deadline resets that deadline on this (AC-192), so a step
    /// that is slow because it is working hard is not failed as stuck. Marshalled to the UI thread by the host.
    /// </remarks>
    event Action? Activity
    {
        add { }
        remove { }
    }
}
