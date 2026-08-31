using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.WorkingPaths;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

// AC-1013: Composes a session-state change into a `SessionStateRecord` and writes it (AC-409); keeps latest-per-pane in memory only to fill in fields an event doesn't carry.
// AC-513: seeds from the store's last record before any write, and `_writeGate` makes compose+persist atomic so concurrent writes land in call order.
// A saved conversation id is scoped to profile+place; see `RecordSessionStartedAsync` for why a change clears it.
public sealed class SessionStateRecorder : ISingletonService
{
    private readonly ISessionStateStore _store;
    private readonly ILogger<SessionStateRecorder> _logger;
    private readonly Dictionary<string, SessionStateRecord> _latest = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    // AC-513: serializes the full "ensure seeded, compose, persist" critical section per _WriteAsync call. SemaphoreSlim
    // releases FIFO, so completion order matches call order, unlike two independent continuations racing after a shared await.
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

    // AC-513: primes the cache from records the caller already loaded, avoiding a second store read on the startup path.
    // No-op once anything has seeded the cache — including `_EnsureSeededAsync`'s own lazy load winning a race,
    // which is strictly newer than this snapshot and must not be overwritten.
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

    // Written once a session has a pane, profile and — if isolation applied — worktree; combines "session started" and "worktree coupled" since worktree resolution is always synchronous with session start.
    // AC-513: a saved conversation id is only safe to reoffer if this write resumes the exact profile+place it was saved against, so a context change clears ConversationId/State back to Unknown.
    // A pane with nothing saved always "changes" harmlessly since a blank record has nothing to lose.
    public Task RecordSessionStartedAsync(
        string paneId,
        SessionProfile profile,
        string? workingDirectory,
        string? worktreePath,
        string? worktreeBranch,
        string? permissionMode,
        // AC-1261 criterion 3: a clear changes neither profile nor working directory, so the context-changed guard
        // below would otherwise leave the saved conversation id standing — and a cockpit closed before any new
        // conversation id arrives would resume the just-cleared one on its next start (V1).
        bool forgetConversation = false,
        CancellationToken cancellationToken = default) =>
        _WriteAsync(paneId, existing =>
        {
            var newProfileId = profile.Label;

            // WorkingDirectory is already the effective, post-isolation path, so comparing it directly also catches a
            // worktree-isolation change. DirectoryPath.Normalize + .Comparison is the same folder-equality rule the worktree
            // engine itself uses; a bare ordinal compare would treat two spellings of the same folder as different and wipe a saved id.
            var contextChanged = forgetConversation
                || !string.Equals(existing.ProfileId, newProfileId, StringComparison.Ordinal)
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

    // Written on a live permission-mode switch (the running SDK panel's dropdown). The mode a session launched with is already captured by `RecordSessionStartedAsync`.
    public Task RecordPermissionModeChangedAsync(string paneId, string permissionMode, CancellationToken cancellationToken = default) =>
        _WriteAsync(paneId, existing => existing with { PermissionMode = permissionMode }, cancellationToken);

    private void _OnConversationChanged(SessionConversationReported reported) =>
        // Fire-and-forget like every other write here: the tracker's event is synchronous, and a session must not
        // stall on a state-file append just because its conversation id changed.
        _ = _WriteAsync(reported.PaneId, existing =>
            // AC-513: Unknown means "no id yet", not "gone" — every session, including a deliberate "start fresh",
            // reports Unknown before reporting anything real, so it must not clobber an already-Known id. Unsupported
            // may still override, since that's the provider stating a fact about itself, not "not yet".
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
        // AC-1013: a real caller token cancelling here can throw OperationCanceledException out of WaitAsync/RecordAsync;
        // unreachable today since both call sites are fire-and-forget with a default token, but a deliberate boundary —
        // unlike a failed seed, which _EnsureSeededAsync always turns into a clean `false` instead of an exception.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await _EnsureSeededAsync().ConfigureAwait(false))
            {
                // AC-513: the store couldn't be read to seed the cache, and composing this write against a blank record
                // risks appending a "fresh" record over whatever the file actually holds. Skip rather than risk the
                // old record; _EnsureSeededAsync already reset _seedTask to null, so the next write retries.
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

    // AC-513: the write path's safety net — loads the store's last-known record per pane exactly once, single-flight
    // (`_writeGate` also caps concurrent callers to one). Cheap after the first success since `_seedTask` stays cached;
    // on a failed read, `_seedTask` resets to null instead of caching the failure, so the next write retries.
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
                // AC-1013: defensive, not currently reachable — only clears the failure this call saw, not whatever _seedTask
                // holds now; today's invariants (single in-flight _EnsureSeededAsync, Seed only assigns over null) guarantee
                // this ReferenceEquals holds. Left in as a cheap guard should those invariants change later.
                if (ReferenceEquals(_seedTask, seedTask))
                {
                    _seedTask = null;
                }
            }
        }

        return seeded;
    }

    // AC-1013: uses CancellationToken.None since this task is shared by every write queued behind _writeGate — one
    // write's token must not decide the outcome for others. Uses TryLoadAsync, not LoadAsync: the latter turns an
    // unreadable file into an empty list, indistinguishable from "no saved state", letting a write bury it.
    private async Task<bool> _LoadAndSeedAsync()
    {
        IReadOnlyList<SessionStateRecord>? states;
        try
        {
            states = await _store.TryLoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // AC-1013: this task is shared by every write queued behind _writeGate and awaited fire-and-forget, so a
            // faulted task here would be cached in _seedTask and every later write would rethrow into
            // `_ = recorder.RecordXAsync(...)`, silently stopping the recorder. Turn it into `false` like a null result instead.
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

    // A plugin provider's own registered id when it has one, else the built-in `SessionProvider`'s name — the same distinction `SessionProfile.Claude` draws for a Claude profile.
    private static string _ProviderId(SessionProfile profile) =>
        profile.ProviderConfig is PluginProviderConfig plugin ? plugin.ProviderId : profile.Provider.ToString();
}
