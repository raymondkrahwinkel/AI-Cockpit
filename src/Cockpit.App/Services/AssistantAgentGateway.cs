using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

/// <summary>
/// The app-level half of <see cref="IAssistantAgentGateway"/> (AC-545): the one place a session is started or
/// stopped on an agent's behalf, and the place every such request is written down.
/// </summary>
/// <remarks>
/// Sibling of <see cref="AssistantReadGateway"/> and shaped like it on purpose — same UI-thread marshalling, for the
/// same reason: <c>CockpitViewModel.Sessions</c> and the workspace settings only ever mutate on the UI thread, and an
/// MCP tool call arrives on a Kestrel request thread.
/// <para>
/// <b>This class refuses; it does not scope.</b> The distinction matters and it is not word-play. Which desk a spawn
/// may land on was decided before this is reached, by whichever of <see cref="SpawnTarget"/>'s two doors the caller
/// came through — that is the guardrail. What happens here is the far duller check that the named desk exists and can
/// hold a session at all, which is true of every caller and protects nobody from anything. Do not let the second grow
/// into the first: a coordinator (AC-436) must arrive with a target derived from its own pane, never with one this
/// class validated on its behalf.
/// </para>
/// <para>
/// <b>Every outcome is recorded, including the refusals.</b> Criterion 5 asks for the trail; a trail that only holds
/// what got through would show the gate's successes and hide it working. A failure to write the trail never fails the
/// operator's approved action — see <see cref="IAssistantSpawnAuditLog"/>.
/// </para>
/// <para>
/// <b>What this is not.</b> It is not the consent gate. Nothing here decides whether the operator agreed: the
/// assistant's session runs in "Ask permissions", so the tool call that reaches this class has already raised an
/// Allow/Deny row in the chat window and been clicked. If a future caller could reach these methods without that,
/// this class would still start the session — which is why the gate belongs where it is and must not be re-implemented
/// here as a second, weaker copy.
/// </para>
/// </remarks>
internal sealed class AssistantAgentGateway(
    CockpitViewModel cockpit,
    ISessionProfileStore profiles,
    IAssistantSpawnAuditLog auditLog) : IAssistantAgentGateway, ISingletonService
{
    public async Task<AgentSpawnResult> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _SpawnAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The tool would have caught this and told the assistant either way. What this adds is the record: a
            // launch that threw is the refusal most worth having in the trail, and it is the one that would
            // otherwise never reach it. The reason is passed on rather than summarised — the operator reading the
            // flyout later has no other trace of it.
            return await _RefuseSpawnAsync(request, workspaceName: null,
                $"Starting that session failed: {exception.Message}", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentSpawnResult> _SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
    {
        // Read off the UI thread before anything is marshalled: this is file-backed and the only genuinely slow step
        // in the whole call.
        var known = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);

        return await _OnUiThreadAsync(async () =>
        {
            var workspace = _FindWorkspace(request.Target.WorkspaceId);
            if (workspace is null)
            {
                return await _RefuseSpawnAsync(request, workspaceName: null,
                    $"There is no workspace with id '{request.Target.WorkspaceId}'. List the workspaces and name one of those.",
                    cancellationToken).ConfigureAwait(true);
            }

            // A dashboard would take the session and never draw it — the pane would run, cost money and be
            // unreachable. Refusing is the kinder half of that pair.
            if (workspace.Type != WorkspaceType.Sessions)
            {
                return await _RefuseSpawnAsync(request, workspace.Name,
                    $"'{workspace.Name}' is a {workspace.Type} desk and cannot show a session. Name a Sessions desk, or ask for a new one to be made.",
                    cancellationToken).ConfigureAwait(true);
            }

            // By label and never by "the first one that looks close": the profile decides provider and model, so a
            // near-miss is a bill the operator did not agree to (AC-436 guardrail 6).
            var profile = known.FirstOrDefault(candidate =>
                string.Equals(candidate.Label, request.ProfileLabel, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                var labels = known.Count == 0 ? "none are configured" : string.Join(", ", known.Select(p => $"'{p.Label}'"));
                return await _RefuseSpawnAsync(request, workspace.Name,
                    $"There is no profile called '{request.ProfileLabel}'. The profiles this cockpit knows are: {labels}.",
                    cancellationToken).ConfigureAwait(true);
            }

            var (requestedKind, kindRefusal) = _ParseKind(request.Kind);
            if (kindRefusal is not null)
            {
                return await _RefuseSpawnAsync(request, workspace.Name, kindRefusal, cancellationToken).ConfigureAwait(true);
            }

            if (requestedKind == SessionKind.Tty && !cockpit.ProfileHasTtyRoute(profile))
            {
                return await _RefuseSpawnAsync(request, workspace.Name,
                    $"'{profile.Label}' has no terminal route of its own, so it can only run as an SDK session.",
                    cancellationToken).ConfigureAwait(true);
            }

            var started = await cockpit.StartSessionOnWorkspaceAsync(
                workspace.Id, profile, request.Prompt, request.WorkingDirectory, request.SessionName, requestedKind).ConfigureAwait(true);

            if (started is not { } pane)
            {
                // Null means the cockpit has no session factories or the launch declined — both are states the
                // operator can be told about, and neither is an exception.
                return await _RefuseSpawnAsync(request, workspace.Name,
                    "The cockpit could not start a session just now.", cancellationToken).ConfigureAwait(true);
            }

            await _RecordAsync(new AssistantSpawnAuditEntry(
                DateTimeOffset.Now,
                AssistantSpawnAction.Start,
                request.Target.Caller,
                request.Target.CallerPaneId,
                workspace.Id,
                workspace.Name,
                profile.Label,
                request.WorkingDirectory,
                pane.PaneId,
                pane.Name,
                Refusal: null), cancellationToken).ConfigureAwait(true);

            return AgentSpawnResult.Started(pane.PaneId, pane.Name, request.WorkingDirectory);
        }).ConfigureAwait(false);
    }

    public async Task<AgentStopResult> StopAsync(string paneId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _StopAsync(paneId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Same reason as SpawnAsync: a teardown that threw is a refusal, and a refusal belongs in the record.
            return await _RefuseStopAsync(paneId, $"Closing that session failed: {exception.Message}", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task<AgentStopResult> _StopAsync(string paneId, CancellationToken cancellationToken) =>
        _OnUiThreadAsync(async () =>
        {
            // First, and by identity rather than by whether it happens to be findable: the assistant is not in
            // Sessions, so today FindSession already misses it — but that is where it sits, not a rule, and a rule is
            // what "the assistant does not end itself mid-sentence" needs to be.
            if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
            {
                return await _RefuseStopAsync(paneId, "That is my own session, and I do not get to end it.", cancellationToken)
                    .ConfigureAwait(true);
            }

            // Looked up in Sessions and NOT through FindSession, which also reaches embedded panes — an Autopilot
            // step, a plugin run. Those are sessions but not panes the cockpit closes: CloseSessionAsync starts with
            // Sessions.IndexOf and returns silently for anything it does not hold. Going through FindSession would
            // therefore have reported a stop that never happened — trail entry, spoken confirmation and all — while
            // the session kept running and kept spending. The assistant's own list_sessions shows these panes, so it
            // is a pane id the model will genuinely offer; it gets a reason instead of a lie.
            if (cockpit.Sessions.FirstOrDefault(candidate => string.Equals(candidate.PaneId, paneId, StringComparison.Ordinal)) is not { } session)
            {
                var elsewhere = cockpit.FindSession(paneId);
                return await _RefuseStopAsync(paneId, elsewhere is null
                        ? $"There is no session with pane id '{paneId}' — it may already have been closed."
                        : $"'{elsewhere.Title}' runs inside a workspace's own surface rather than as a pane, so I cannot close it. Whoever started it ends it.",
                    cancellationToken).ConfigureAwait(true);
            }

            // The same "is there an agent on the other end" test the read gateway lists by, so what can be stopped is
            // exactly what could be seen. A plain terminal has a pane id and no agent.
            if (!session.ShowPluginHeaderItems)
            {
                return await _RefuseStopAsync(paneId, $"'{session.Title}' is a terminal pane, not an agent session.", cancellationToken)
                    .ConfigureAwait(true);
            }

            var name = session.Title;
            var workspaceId = session.WorkspaceId;
            await cockpit.StopSessionForAssistantAsync(session).ConfigureAwait(true);

            await _RecordAsync(new AssistantSpawnAuditEntry(
                DateTimeOffset.Now,
                AssistantSpawnAction.Stop,
                SpawnCaller.Assistant,
                CallerPaneId: null,
                workspaceId ?? string.Empty,
                _FindWorkspace(workspaceId)?.Name,
                session.ActiveProfileLabel,
                WorkingDirectory: null,
                paneId,
                name,
                Refusal: null), cancellationToken).ConfigureAwait(true);

            return AgentStopResult.Stopped(paneId, name);
        });

    public Task<IReadOnlyList<AssistantWorkspaceRow>> ListWorkspacesAsync(CancellationToken cancellationToken = default) =>
        _OnUiThreadAsync(() => Task.FromResult(_ListWorkspaces()));

    /// <summary>
    /// The profiles, straight off the store. No UI thread: this reads a file, not the cockpit's collections.
    /// </summary>
    public async Task<IReadOnlyList<AssistantProfileRow>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        var known = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. known.Select(profile => new AssistantProfileRow(
                profile.Label,
                profile.Provider.ToString(),
                ProfileDisplay.ModelOf(profile))),
        ];
    }

    public async Task<AssistantWorkspaceRow?> CreateWorkspaceAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return await _OnUiThreadAsync(async () =>
        {
            var created = await cockpit.Workspaces.CreateSessionsWorkspaceAsync(trimmed).ConfigureAwait(true);
            return (AssistantWorkspaceRow?)new AssistantWorkspaceRow(
                created.Id, created.Name, created.Type.ToString(), CanHostSessions: true, SessionCount: 0, IsActive: true);
        }).ConfigureAwait(false);
    }

    private IReadOnlyList<AssistantWorkspaceRow> _ListWorkspaces()
    {
        var settings = cockpit.Workspaces.Settings;
        var firstSessionsWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(settings);

        // Counted the way the read path places sessions (AC-543), not by trusting each session's own stamp: a session
        // the placement rule puts on the fallback desk is on that desk to everyone else, and a roster that disagreed
        // with the sidebar would be a roster nobody could act on. The assistant is excluded — it is the one asking,
        // and it sits on no desk to be counted on.
        var counts = cockpit.AllSessions()
            .Where(session => session.ShowPluginHeaderItems
                && !string.Equals(session.PaneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
            .Select(session => SessionWorkspacePlacement.Resolve(session, firstSessionsWorkspaceId))
            .Where(id => id is not null)
            .GroupBy(id => id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return
        [
            .. settings.Workspaces.Select(workspace => new AssistantWorkspaceRow(
                workspace.Id,
                workspace.Name,
                workspace.Type.ToString(),
                workspace.Type == WorkspaceType.Sessions,
                counts.TryGetValue(workspace.Id, out var count) ? count : 0,
                string.Equals(workspace.Id, settings.Active?.Id, StringComparison.Ordinal))),
        ];
    }

    /// <summary>
    /// The route asked for, or null for "whatever the profile is set to". A word that is neither is refused rather
    /// than read as the default: the operator said a route out loud, and starting the other one would look like it
    /// worked. "cli" and "terminal" are accepted for tty because those are the words people actually say.
    /// </summary>
    private static (SessionKind? Kind, string? Refusal) _ParseKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            null or "" => (null, null),
            "sdk" => (SessionKind.Sdk, null),
            "tty" or "cli" or "terminal" => (SessionKind.Tty, null),
            var other => (null, $"'{other}' is not a route I know — it is either sdk or tty."),
        };

    private Workspace? _FindWorkspace(string? workspaceId) =>
        workspaceId is null
            ? null
            : cockpit.Workspaces.Settings.Workspaces.FirstOrDefault(
                workspace => string.Equals(workspace.Id, workspaceId, StringComparison.Ordinal));

    private async Task<AgentSpawnResult> _RefuseSpawnAsync(
        AgentSpawnRequest request, string? workspaceName, string reason, CancellationToken cancellationToken)
    {
        await _RecordAsync(new AssistantSpawnAuditEntry(
            DateTimeOffset.Now,
            AssistantSpawnAction.Start,
            request.Target.Caller,
            request.Target.CallerPaneId,
            request.Target.WorkspaceId,
            workspaceName,
            request.ProfileLabel,
            request.WorkingDirectory,
            PaneId: null,
            SessionName: null,
            reason), cancellationToken).ConfigureAwait(true);

        return AgentSpawnResult.Refused(reason);
    }

    private async Task<AgentStopResult> _RefuseStopAsync(string paneId, string reason, CancellationToken cancellationToken)
    {
        await _RecordAsync(new AssistantSpawnAuditEntry(
            DateTimeOffset.Now,
            AssistantSpawnAction.Stop,
            SpawnCaller.Assistant,
            CallerPaneId: null,
            WorkspaceId: string.Empty,
            WorkspaceName: null,
            Profile: null,
            WorkingDirectory: null,
            paneId,
            SessionName: null,
            reason), cancellationToken).ConfigureAwait(true);

        return AgentStopResult.Refused(reason);
    }

    private Task _RecordAsync(AssistantSpawnAuditEntry entry, CancellationToken cancellationToken) =>
        auditLog.RecordAsync(entry, cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread — inline when already there, so a test on the UI thread pays for
    /// no redundant dispatch. Same rule as <see cref="AssistantReadGateway"/>, awaited rather than blocked on.
    /// </summary>
    private static Task<T> _OnUiThreadAsync<T>(Func<Task<T>> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);
}
