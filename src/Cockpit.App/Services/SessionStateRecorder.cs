using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.WorkingPaths;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

/// <summary>
/// The one place that knows how to turn a session-state change into a <see cref="SessionStateRecord"/> and write
/// it (AC-409), instead of every call site in <c>CockpitViewModel</c>/<c>SessionViewModel</c> composing half of one
/// itself. Keeps the latest record per pane in memory purely to fill in the fields a given event does not carry —
/// a reported conversation id (AC-408) knows nothing about the pane's working directory — so each write still
/// appends a complete record.
/// <para>
/// AC-513: this in-memory copy starts empty on every process start, so the first write for a pane this run must
/// not be composed against a blank record when the store's own file already has a real one — that would append a
/// "fresh" record over it and, since <see cref="ISessionStateStore.LoadAsync"/> is last-record-wins, bury the
/// saved conversation id for good. <see cref="_EnsureSeededAsync"/> loads the store's last-known record per pane
/// before any write proceeds. <see cref="Seed"/> lets <c>CockpitViewModel.RestoreSessionPanesAsync</c> hand in the
/// same list it already read for restore planning, so this does not cost a second file read on the common path —
/// but a write reaching this class before that call lands still self-heals via <see cref="_EnsureSeededAsync"/>,
/// which is what makes the guarantee hold regardless of startup ordering.
/// </para>
/// <para>
/// Composing a record and persisting it are one uninterruptible step, guarded end to end by
/// <see cref="_writeGate"/>: a write that started earlier always finishes (both the compose and the
/// <see cref="ISessionStateStore.RecordAsync"/> call) before a later write is allowed to even begin composing. Two
/// writes for the same pane that arrive close together — a driver reporting <c>Unknown</c> immediately followed by
/// its real conversation id — must land on disk in the order they were made, or the store's last-record-wins read
/// would keep whichever one happened to reach the disk append second, regardless of which was actually the newer
/// information.
/// </para>
/// <para>
/// A saved conversation id is scoped to the profile and place it was saved under — see
/// <see cref="RecordSessionStartedAsync"/> for why a session started under a different one clears it rather than
/// letting a "start fresh" offer resume it somewhere it never actually ran.
/// </para>
/// </summary>
public sealed class SessionStateRecorder : ISingletonService
{
    private readonly ISessionStateStore _store;
    private readonly ILogger<SessionStateRecorder> _logger;
    private readonly Dictionary<string, SessionStateRecord> _latest = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    // AC-513: serializes the full "ensure seeded, compose, persist" critical section per _WriteAsync call, taken
    // before _EnsureSeededAsync and released only after _store.RecordAsync returns — see the class doc. SemaphoreSlim
    // releases FIFO, so completion order matches call order, unlike two independent continuations racing after a
    // shared await.
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Task<bool>? _seedTask;

    public SessionStateRecorder(ISessionStateStore store, SessionConversationTracker conversationTracker, ILogger<SessionStateRecorder> logger)
    {
        _store = store;
        _logger = logger;

        // AC-408's tracker already suppresses a report that repeats the same conversation id, so every invocation
        // here is a real change worth a line — never a per-turn poll.
        conversationTracker.Changed += _OnConversationChanged;
    }

    /// <summary>
    /// AC-513: primes the in-memory cache from records the caller already loaded (<c>RestoreSessionPanesAsync</c>'s
    /// own <c>ISessionStateStore.LoadAsync</c> call), so the common startup path does not read the store file a
    /// second time. A no-op once anything has already seeded the cache — including <see cref="_EnsureSeededAsync"/>'s
    /// own lazy load, which can win the race when a write for an unseeded pane arrives before this is called;
    /// that load is strictly newer than whatever snapshot this method was handed, so it must not be overwritten.
    /// </summary>
    public void Seed(IReadOnlyList<SessionStateRecord> states)
    {
        lock (_gate)
        {
            if (_seedTask is not null)
            {
                return;
            }

            foreach (var state in states)
            {
                _latest[state.PaneId] = state;
            }

            _seedTask = Task.FromResult(true);
        }
    }

    /// <summary>
    /// Written once a session has a pane, a resolved profile and — if isolation applied — its worktree: everything
    /// <c>CockpitViewModel._LaunchSessionFromResultAsync</c> has settled by the time a session is usable. Combines
    /// what the ticket calls "session started" and "worktree coupled" into one record because, in this codebase,
    /// worktree resolution always happens synchronously as part of starting the session — there is no later moment
    /// where an already-running session gains a worktree it did not start with.
    /// <para>
    /// AC-513: a "start fresh" on a restored pane relies on the saved id surviving right up until the new
    /// conversation reports its own (see <see cref="_OnConversationChanged"/>'s guard) — but a saved id is only
    /// safe to keep offering if this write is resuming the exact profile and place it was saved against;
    /// otherwise nothing says the provider on the other end of that id would even recognise this pane's new
    /// context, and a crash before the new conversation reports anything would offer "conv-old" under a profile
    /// or in a directory it never actually ran in. So, unlike every other field here, <c>ConversationId</c>/
    /// <c>ConversationState</c> are not simply carried forward: a write whose profile or effective working
    /// directory differs from what is already saved clears them back to
    /// <see cref="SessionConversationIdState.Unknown"/>, the same "not known in this context" state a fresh
    /// session starts in. A pane with nothing saved yet (a genuinely new pane) compares its blank
    /// <c>ProfileId</c>/<c>WorkingDirectory</c> against this write's real ones and always "changes" — harmlessly,
    /// since a blank record already carries a null id and <c>Unknown</c> state; there is nothing this could lose.
    /// </para>
    /// </summary>
    public Task RecordSessionStartedAsync(
        string paneId,
        SessionProfile profile,
        string? workingDirectory,
        string? worktreePath,
        string? worktreeBranch,
        string? permissionMode,
        CancellationToken cancellationToken = default) =>
        _WriteAsync(paneId, existing =>
        {
            var newProfileId = profile.Label;

            // WorkingDirectory is already the effective, post-isolation path (see SessionStateRecord's own doc on
            // that field) — the isolated worktree's path when isolation applied, else the plain folder — so
            // comparing it directly also catches a worktree-isolation change without a separate WorktreePath
            // comparison. DirectoryPath.Normalize + .Comparison is the same folder-equality rule the worktree
            // engine itself uses (case-insensitive on Windows/macOS, exact on Linux); a bare ordinal string
            // compare would treat two spellings of the same folder — a trailing separator, a relative segment —
            // as two different places and wipe a saved id that never actually moved.
            var contextChanged = !string.Equals(existing.ProfileId, newProfileId, StringComparison.Ordinal)
                || !string.Equals(DirectoryPath.Normalize(existing.WorkingDirectory), DirectoryPath.Normalize(workingDirectory), DirectoryPath.Comparison);

            return existing with
            {
                ProfileId = newProfileId,
                ProviderId = _ProviderId(profile),
                WorkingDirectory = workingDirectory,
                WorktreePath = worktreePath,
                WorktreeBranch = worktreeBranch,
                PermissionMode = permissionMode,
                ConversationId = contextChanged ? null : existing.ConversationId,
                ConversationState = contextChanged ? SessionConversationIdState.Unknown : existing.ConversationState,
            };
        }, cancellationToken);

    /// <summary>Written on a live permission-mode switch (the running SDK panel's dropdown). The mode a session launched with is already captured by <see cref="RecordSessionStartedAsync"/>.</summary>
    public Task RecordPermissionModeChangedAsync(string paneId, string permissionMode, CancellationToken cancellationToken = default) =>
        _WriteAsync(paneId, existing => existing with { PermissionMode = permissionMode }, cancellationToken);

    private void _OnConversationChanged(SessionConversationReported reported) =>
        // Fire-and-forget like every other write here: the tracker's event is synchronous, and a session must not
        // stall on a state-file append just because its conversation id changed.
        _ = _WriteAsync(reported.PaneId, existing =>
            // AC-513: Unknown is "no id yet", not "the id is gone" — every session, including a deliberate "start
            // fresh" after a restart, reports Unknown before it ever reports anything real (IPluginSessionDriver's
            // default Conversation property). Raymond's call: the saved id stays until the newly started
            // conversation reports one of its own, so an Unknown report must not clobber an already-Known one.
            // Unsupported is left free to override — that is the provider stating a fact about itself, not "not
            // yet", and a stale Known id from a previous provider on this pane would be dishonest to keep offering.
            reported.Conversation.State == SessionConversationIdState.Unknown
            && existing.ConversationState == SessionConversationIdState.Known
                ? existing
                : existing with
                {
                    ConversationId = reported.Conversation.Value,
                    ConversationState = reported.Conversation.State,
                }, CancellationToken.None);

    private async Task _WriteAsync(string paneId, Func<SessionStateRecord, SessionStateRecord> mutate, CancellationToken cancellationToken)
    {
        // A caller that passes a real (non-default) token and then cancels it can still see this throw
        // OperationCanceledException back out of WaitAsync/_store.RecordAsync below — both call sites today are
        // fire-and-forget with a default token (`_ = recorder.RecordXAsync(...)`), so it is not reachable in
        // production, but it is a deliberate boundary, not an oversight: a cancelled write is the caller's own
        // token expiring on its own request, unlike a failed seed, which _EnsureSeededAsync always turns into a
        // clean `false` rather than an exception.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await _EnsureSeededAsync().ConfigureAwait(false))
            {
                // AC-513: the store could not be read to seed the cache, and composing this write against a blank
                // record would risk appending a "fresh" record over whatever the file actually holds — the same
                // loss criterion 2 exists to prevent, just triggered by an unreadable file instead of an empty
                // cache. Skipping costs only this write's own information; writing through would risk the old
                // record's. _EnsureSeededAsync already put _seedTask back to null, so the next write tries again.
                _logger.LogWarning("Skipped a session-state write for pane {PaneId}: the store could not be read to seed the write cache.", paneId);
                return;
            }

            SessionStateRecord updated;
            lock (_gate)
            {
                var existing = _latest.TryGetValue(paneId, out var found)
                    ? found
                    : new SessionStateRecord(paneId, null, null, null, SessionConversationIdState.Unknown, null, null, null, null, DateTimeOffset.UtcNow);

                updated = mutate(existing) with { RecordedAt = DateTimeOffset.UtcNow };
                _latest[paneId] = updated;
            }

            await _store.RecordAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// AC-513: the write path's own safety net — loads the store's last-known record per pane exactly once
    /// (concurrent callers would await the same in-flight load, single-flight; in practice <see cref="_writeGate"/>
    /// already means at most one caller is ever inside this method at a time). Cheap after the first successful
    /// call: <see cref="_seedTask"/> stays set, so every later write reuses the already-completed result. Returns
    /// <see langword="false"/> if the store could not be read — <see cref="_seedTask"/> is put back to
    /// <see langword="null"/> in that case rather than caching the failure, so the next write tries the read
    /// again instead of every write for the rest of the process silently doing nothing.
    /// </summary>
    private async Task<bool> _EnsureSeededAsync()
    {
        Task<bool> seedTask;
        lock (_gate)
        {
            _seedTask ??= _LoadAndSeedAsync();
            seedTask = _seedTask;
        }

        var seeded = await seedTask.ConfigureAwait(false);
        if (!seeded)
        {
            lock (_gate)
            {
                // Defensive, not currently reachable: only clear the failure this call actually saw, not
                // whatever _seedTask happens to hold by the time this runs. Under today's invariants nothing
                // else can have changed it in between — _writeGate means only one _EnsureSeededAsync call is
                // ever in flight at a time, and Seed() only ever assigns _seedTask when it observes null, never
                // over an existing value — so this ReferenceEquals can never actually be false. Left in as a
                // cheap guard against that no longer being true after a future change (e.g. _writeGate's
                // exclusivity being relaxed, or a second caller of this method appearing) rather than trusted to
                // stay true forever; not worth a production-code test seam to force a scenario that cannot
                // currently occur.
                if (ReferenceEquals(_seedTask, seedTask))
                {
                    _seedTask = null;
                }
            }
        }

        return seeded;
    }

    /// <summary>
    /// The load behind <see cref="_EnsureSeededAsync"/>. Always reads with <see cref="CancellationToken.None"/>:
    /// this task is shared by whichever writes are queued behind <see cref="_writeGate"/> at the time, and the
    /// first write's own cancellation token must not decide the outcome for writes that never asked to be
    /// cancelled. Uses <see cref="ISessionStateStore.TryLoadAsync"/> rather than <see cref="ISessionStateStore.LoadAsync"/>:
    /// the latter turns an unreadable file into an empty list, which here would look identical to "this pane
    /// genuinely has no saved state" and let a write bury it.
    /// </summary>
    private async Task<bool> _LoadAndSeedAsync()
    {
        IReadOnlyList<SessionStateRecord>? states;
        try
        {
            states = await _store.TryLoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // This task is shared by every write queued behind _writeGate, and it is awaited on a fire-and-forget
            // path — so a store that throws where its contract says it returns null must not become a faulted task.
            // That task would be cached in _seedTask (the reset below only runs for a clean `false`), every later
            // write would await the same faulted task and rethrow into `_ = recorder.RecordXAsync(...)`, and the
            // recorder would stop writing for the rest of the process without anything surfacing. Turning it into
            // the same `false` a null result gets keeps the retry-on-next-write behaviour intact whichever way the
            // read failed.
            _logger.LogWarning(exception, "Reading the session-state log to seed the write cache threw; the next write will try again.");
            return false;
        }

        if (states is null)
        {
            _logger.LogWarning("Could not read the session-state log to seed the write cache; the next write will try again.");
            return false;
        }

        lock (_gate)
        {
            // Every write awaits _EnsureSeededAsync before it ever touches _latest (see _WriteAsync), and Seed()
            // backs off once _seedTask is set — so this is the only place allowed to populate _latest from the
            // process's first successful seed. A plain overwrite is correct, not just convenient.
            foreach (var state in states)
            {
                _latest[state.PaneId] = state;
            }
        }

        return true;
    }

    /// <summary>A plugin provider's own registered id when it has one, else the built-in <see cref="SessionProvider"/>'s name — the same distinction <see cref="SessionProfile.Claude"/> draws for a Claude profile.</summary>
    private static string _ProviderId(SessionProfile profile) =>
        profile.ProviderConfig is PluginProviderConfig plugin ? plugin.ProviderId : profile.Provider.ToString();
}
