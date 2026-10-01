using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Where a session the launcher starts runs: the backend's own host, or a frontend's pane (AC-1439).
/// The launcher decides and orders the start; this only makes the session and its handle.
/// </summary>
public interface ISessionHosting
{
    /// <summary>
    /// Whether this host can run a session of <paramref name="kind"/> at all; asked before anything is admitted.
    /// </summary>
    bool CanHost(PaneSessionKind kind);

    /// <summary>
    /// The session <paramref name="request"/> describes, not yet registered or started.
    /// Null when this host cannot run that kind of session.
    /// </summary>
    IHostedSession? Host(SessionHostingRequest request);
}

/// <summary>
/// A session made by <see cref="ISessionHosting"/>, as the registry holds it.
/// The one owner of its host, so nothing else pumps or folds that host's events (AC-1439).
/// </summary>
public interface IHostedSession : ISessionHandle
{
    /// <summary>
    /// True for a session that is registered but not started, such as a pane a restart brought back (AC-410).
    /// A launch naming its pane id starts this one rather than hosting another.
    /// </summary>
    bool AwaitsStart { get; }

    /// <summary>
    /// Repaints a resumed conversation's recorded log, or rolls it aside for a new one (AC-1080).
    /// </summary>
    Task PrepareRecordedTranscriptAsync(SessionResume resume, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the session in <paramref name="workingDirectory"/>, the folder its admission settled.
    /// Throws when the start failed and the session should go; a pane that shows its own failure returns.
    /// </summary>
    Task StartAsync(SessionLaunchRequest request, string? workingDirectory);

    /// <summary>
    /// Ends the session and lets go of what it holds.
    /// </summary>
    Task StopAsync();
}

// AC-1439: one session to host, as the launcher settled it: its id, name, kind and the folder and branch admission gave.
public sealed record SessionHostingRequest(
    string PaneId,
    string Name,
    bool NameIsChosen,
    PaneSessionKind Kind,
    SessionLaunchRequest Request,
    string? WorkingDirectory,
    string? WorktreeBranch);
