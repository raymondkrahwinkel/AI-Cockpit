using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Persists <see cref="SessionStateRecord"/>s to <c>session-state.jsonl</c> (AC-409): a write-on-change log of
/// what each pane needs to restart after a crash. Not built on <c>JsonlAuditLog{T}</c> — its uneraseable-record
/// contract clashes with <see cref="CompactAsync"/>'s deliberate rewrite; unlike <c>CockpitConfigFileAccess</c>, unreadable state need not block start.
/// </summary>
public interface ISessionStateStore
{
    /// <summary>
    /// Appends one record when something actually changes — a session starting, a worktree resolving, a
    /// permission-mode switch — never on a timer or at shutdown, so a crash still leaves the latest state on
    /// disk. Never throws: a failed write is logged and the record lost, not the caller's action.
    /// </summary>
    Task RecordAsync(SessionStateRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest record per pane, read forward across the file (last one wins). Never throws: a
    /// missing/unreadable/half-written file yields whatever parses, with a warning logged. Collapses "no state"
    /// and "could not read" into one empty result — right for a restore (see <see cref="TryLoadAsync"/> to split them).
    /// </summary>
    Task<IReadOnlyList<SessionStateRecord>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Same read as <see cref="LoadAsync"/> but keeps "no state" and "could not tell" apart: <see
    /// langword="null"/> on a failed read/parse, a genuinely real list otherwise. Needed by a caller composing a
    /// write (AC-513's <c>SessionStateRecorder</c>) — treating a failed read as "empty" would bury real data.
    /// </summary>
    Task<IReadOnlyList<SessionStateRecord>?> TryLoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites the file to at most one record per pane, folding older records into the latest. <paramref
    /// name="knownPaneIds"/> names surviving panes; others drop. <see langword="null"/> folds and drops nothing
    /// — deriving the set from this store's own records instead would read twice and could delete a session that just started.
    /// </summary>
    Task CompactAsync(IReadOnlySet<string>? knownPaneIds = null, CancellationToken cancellationToken = default);
}
