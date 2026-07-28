using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// The one place that knows how to turn a session-state change into a <see cref="SessionStateRecord"/> and write
/// it (AC-409), instead of every call site in <c>CockpitViewModel</c>/<c>SessionViewModel</c> composing half of one
/// itself. Keeps the latest record per pane in memory purely to fill in the fields a given event does not carry —
/// a reported conversation id (AC-408) knows nothing about the pane's working directory — so each write still
/// appends a complete record. The in-memory copy is not the source of truth for a restart: <see cref="ISessionStateStore.LoadAsync"/>
/// reading the file back is.
/// </summary>
public sealed class SessionStateRecorder : ISingletonService
{
    private readonly ISessionStateStore _store;
    private readonly Dictionary<string, SessionStateRecord> _latest = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public SessionStateRecorder(ISessionStateStore store, SessionConversationTracker conversationTracker)
    {
        _store = store;

        // AC-408's tracker already suppresses a report that repeats the same conversation id, so every invocation
        // here is a real change worth a line — never a per-turn poll.
        conversationTracker.Changed += _OnConversationChanged;
    }

    /// <summary>
    /// Written once a session has a pane, a resolved profile and — if isolation applied — its worktree: everything
    /// <c>CockpitViewModel._LaunchSessionFromResultAsync</c> has settled by the time a session is usable. Combines
    /// what the ticket calls "session started" and "worktree coupled" into one record because, in this codebase,
    /// worktree resolution always happens synchronously as part of starting the session — there is no later moment
    /// where an already-running session gains a worktree it did not start with.
    /// </summary>
    public Task RecordSessionStartedAsync(
        string paneId,
        SessionProfile profile,
        string? workingDirectory,
        string? worktreePath,
        string? worktreeBranch,
        string? permissionMode,
        CancellationToken cancellationToken = default) =>
        _WriteAsync(paneId, existing => existing with
        {
            ProfileId = profile.Label,
            ProviderId = _ProviderId(profile),
            WorkingDirectory = workingDirectory,
            WorktreePath = worktreePath,
            WorktreeBranch = worktreeBranch,
            PermissionMode = permissionMode,
        }, cancellationToken);

    /// <summary>Written on a live permission-mode switch (the running SDK panel's dropdown). The mode a session launched with is already captured by <see cref="RecordSessionStartedAsync"/>.</summary>
    public Task RecordPermissionModeChangedAsync(string paneId, string permissionMode, CancellationToken cancellationToken = default) =>
        _WriteAsync(paneId, existing => existing with { PermissionMode = permissionMode }, cancellationToken);

    private void _OnConversationChanged(SessionConversationReported reported) =>
        // Fire-and-forget like every other write here: the tracker's event is synchronous, and a session must not
        // stall on a state-file append just because its conversation id changed.
        _ = _WriteAsync(reported.PaneId, existing => existing with
        {
            ConversationId = reported.Conversation.Value,
            ConversationState = reported.Conversation.State,
        }, CancellationToken.None);

    private Task _WriteAsync(string paneId, Func<SessionStateRecord, SessionStateRecord> mutate, CancellationToken cancellationToken)
    {
        SessionStateRecord updated;
        lock (_gate)
        {
            var existing = _latest.TryGetValue(paneId, out var found)
                ? found
                : new SessionStateRecord(paneId, null, null, null, SessionConversationIdState.Unknown, null, null, null, null, DateTimeOffset.UtcNow);

            updated = mutate(existing) with { RecordedAt = DateTimeOffset.UtcNow };
            _latest[paneId] = updated;
        }

        return _store.RecordAsync(updated, cancellationToken);
    }

    /// <summary>A plugin provider's own registered id when it has one, else the built-in <see cref="SessionProvider"/>'s name — the same distinction <see cref="SessionProfile.Claude"/> draws for a Claude profile.</summary>
    private static string _ProviderId(SessionProfile profile) =>
        profile.ProviderConfig is PluginProviderConfig plugin ? plugin.ProviderId : profile.Provider.ToString();
}
