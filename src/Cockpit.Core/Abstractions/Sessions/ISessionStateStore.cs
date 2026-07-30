using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Persists <see cref="SessionStateRecord"/>s to <c>session-state.jsonl</c> (AC-409): a write-on-change log of
/// what each pane needs to be brought back after a restart or a crash. Deliberately not built on
/// <c>JsonlAuditLog{T}</c> — that base's whole contract is that a record, once logged, cannot be erased, and this
/// store's <see cref="CompactAsync"/> rewrites the file on purpose. This is derived state a session can start
/// without, not a trail an operator or an agent must not be able to clear, so a store that cannot be read is not
/// a reason to refuse to start (contrast <c>CockpitConfigFileAccess</c>, which is).
/// </summary>
public interface ISessionStateStore
{
    /// <summary>
    /// Appends one record. Called at the moment something actually changes — a session starting, a worktree
    /// resolving, a reported conversation id, a live permission-mode switch — never on a timer and never at
    /// shutdown, so a crash still leaves the latest state on disk. Never throws: a write that fails is logged and
    /// the record is lost, not the caller's action.
    /// </summary>
    Task RecordAsync(SessionStateRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest record per pane, read forward across the whole file (last one for a given pane wins). Never
    /// throws: a missing or unreadable file, or one with a half-written last line, yields whatever could be parsed
    /// — empty in the worst case — with a warning logged, not an exception.
    /// <para>
    /// This collapses "the file has no state" and "the file could not be read" into the same empty result, which is
    /// the right answer for a restore that has no other fallback (see <see cref="TryLoadAsync"/> for a caller that
    /// needs to tell them apart).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<SessionStateRecord>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Same read as <see cref="LoadAsync"/>, but keeps "no state" and "could not tell" apart instead of collapsing
    /// them: returns <see langword="null"/> when the file could not be read or nothing in it could be parsed, and a
    /// (possibly empty) list only when the read genuinely succeeded. Empty on a missing file — that is a real
    /// answer, not a failure. For a caller composing a write on top of the result (AC-513's
    /// <c>SessionStateRecorder</c>), treating a failed read as "empty" would let that write bury whatever the file
    /// actually holds under a blank record; <see cref="LoadAsync"/>'s collapse is only safe for a caller, like a
    /// restore, that has no write to protect and nothing better to fall back on than "no state".
    /// </summary>
    Task<IReadOnlyList<SessionStateRecord>?> TryLoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites the file to hold at most one record per pane, folding a pane's older records into its latest one.
    /// <para>
    /// <paramref name="knownPaneIds"/> names the panes that still exist; a pane outside it is dropped. Pass
    /// <see langword="null"/> to fold duplicates and drop nothing — which is what a caller that has no trustworthy
    /// roster must do, and what the cockpit does today, because an AI session's pane is not yet persisted anywhere
    /// to enumerate. Deriving the set from this store's own records instead would look equivalent and is not: it
    /// reads the file twice, and a session that starts between those two reads is present in the second and absent
    /// from the set, so compaction would delete the state of the session that had just begun.
    /// </para>
    /// </summary>
    Task CompactAsync(IReadOnlySet<string>? knownPaneIds = null, CancellationToken cancellationToken = default);
}
