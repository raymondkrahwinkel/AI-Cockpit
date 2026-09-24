using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Worktrees;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Assistant;

// AC-545/AC-436: records every spawn/stop outcome, refusals included, and enforces that the named desk can hold a
// session; which desk (`SpawnTarget`) and consent (an Allow/Deny row) are settled upstream. AC-1375: multi-field
// decisions run in `ISessionLauncher.RunExclusiveAsync`, and the action after it re-checks its own precondition.
internal sealed class AssistantAgentGateway(
    ISessionRegistry sessions,
    ISessionLauncher launcher,
    IProjectEditor projectEditor,
    ISessionWatcher watcher,
    IAssistantConversation conversation,
    IExternalLinkOpener links,
    ISessionProfileStore profiles,
    IAssistantSpawnAuditLog auditLog,
    IWorkspaceAgentGateway agents,
    IAgentMessageInbox inbox,
    IAgentNotifyAuditLog notifyAudit,
    IPluginProviderRegistry pluginProviders,
    IWorktreeManager? worktreeManager = null,
    IProjectFieldRegistry? projectFields = null) : IAssistantAgentGateway, ISingletonService
{
    private static readonly StringComparison _PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;


    public async Task<AgentSpawnResult> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _SpawnAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A launch that threw is the refusal most worth recording — it would otherwise never reach the
            // trail. The reason is passed on rather than summarised — the operator reading the flyout later
            // has no other trace of it.
            return await _RefuseSpawnAsync(request, workspaceName: null,
                $"Starting that session failed: {exception.Message}", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentSpawnResult> _SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
    {
        // Read before anything else: this is file-backed and the only genuinely slow step in the whole call.
        var known = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);

        // The desk is the one thing read from shared state; the start below checks it again as the pane lands, since
        // resolving the project in between gives a close the chance to run.
        var workspace = await launcher.RunExclusiveAsync(() => _FindWorkspace(request.Target.WorkspaceId)).ConfigureAwait(false);
        if (workspace is null)
        {
            return await _RefuseSpawnAsync(request, workspaceName: null,
                $"There is no workspace with id '{request.Target.WorkspaceId}'. List the workspaces and name one of those.",
                cancellationToken).ConfigureAwait(false);
        }

        // A dashboard would take the session and never draw it — the pane would run, cost money and be
        // unreachable. Refusing is the kinder half of that pair.
        if (workspace.Type != WorkspaceType.Sessions)
        {
            return await _RefuseSpawnAsync(request, workspace.Name,
                $"'{workspace.Name}' is a {workspace.Type} desk and cannot show a session. Name a Sessions desk, or ask for a new one to be made.",
                cancellationToken).ConfigureAwait(false);
        }

        // AC-773: the project named by id, if any — looked up via `ISessionLauncher.FindProjectByIdAsync`, never
        // re-derived here. An unknown id is refused rather than silently falling back to a folder guess.
        Project? project = null;
        if (request.ProjectId is { Length: > 0 } requestedProjectId)
        {
            project = await launcher.FindProjectByIdAsync(requestedProjectId).ConfigureAwait(false);
            if (project is null)
            {
                return await _RefuseSpawnAsync(request, workspace.Name,
                    $"There is no project with id '{requestedProjectId}'. Call list_projects and name one of those.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // The label to look up: the caller's own, or — only when they left it out — the resolved project's
        // default (AC-773). An explicit label always wins; it is never merged with or overruled by the project.
        var profileLabel = string.IsNullOrWhiteSpace(request.ProfileLabel) ? project?.DefaultProfileLabel : request.ProfileLabel;
        if (string.IsNullOrWhiteSpace(profileLabel))
        {
            return await _RefuseSpawnAsync(request, workspace.Name,
                project is null
                    ? "A profile is required; name one or give a projectId whose DefaultProfileLabel can be used."
                    : $"Project '{project.Name}' has no DefaultProfileLabel set, so a profile must be named explicitly.",
                cancellationToken).ConfigureAwait(false);
        }

        // By label and never by "the first one that looks close": the profile decides provider and model, so a
        // near-miss is a bill the operator did not agree to (AC-436 guardrail 6).
        var profile = known.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, profileLabel, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            var labels = known.Count == 0 ? "none are configured" : string.Join(", ", known.Select(p => $"'{p.Label}'"));
            return await _RefuseSpawnAsync(request, workspace.Name,
                $"There is no profile called '{profileLabel}'. The profiles this cockpit knows are: {labels}.",
                cancellationToken).ConfigureAwait(false);
        }

        var (requestedKind, kindRefusal) = _ParseKind(request.Kind);
        if (kindRefusal is not null)
        {
            return await _RefuseSpawnAsync(request, workspace.Name, kindRefusal, cancellationToken).ConfigureAwait(false);
        }

        if (requestedKind == PaneSessionKind.Tty && !launcher.ProfileHasTtyRoute(profile))
        {
            return await _RefuseSpawnAsync(request, workspace.Name,
                $"'{profile.Label}' has no terminal route of its own, so it can only run as an SDK session.",
                cancellationToken).ConfigureAwait(false);
        }

        // AC-648/AC-649: checked against the provider's own declared capabilities, not a list kept here — an
        // unknown key is refused with a reason instead of reaching the CLI as a flag it doesn't take.
        // `permission-mode` is always refused — see `SpawnOptionOverrides.NeverOverridable`.
        var registration = profile.ProviderConfig is PluginProviderConfig plugin
            ? pluginProviders.Resolve(plugin.ProviderId)
            : null;
        var (launchOptions, optionRefusal) = SpawnOptionOverrides.Merge(
            registration?.DisplayName ?? profile.Provider.ToString(),
            registration?.Capabilities,
            profile.Defaults?.OptionDefaults,
            request.OptionOverrides);
        if (optionRefusal is not null)
        {
            return await _RefuseSpawnAsync(request, workspace.Name, optionRefusal, cancellationToken).ConfigureAwait(false);
        }

        // AC-719: refused categorically, like permission-mode — a caller that could dial isolation down per
        // spawn is one hop from the working-tree contamination isolation exists to prevent.
        if (request.IsolateInWorktree == false)
        {
            return await _RefuseSpawnAsync(request, workspace.Name,
                "'isolate: false' is not something a spawn may ask for — that would run it in the operator's real "
                + "checkout. Leave it out to use the project's own isolation setting, or ask for isolate: true.",
                cancellationToken).ConfigureAwait(false);
        }

        var started = await launcher.StartSessionAsync(new SessionLaunchRequest(
            workspace.Id, profile, request.Prompt, request.WorkingDirectory, request.SessionName, requestedKind,
            launchOptions, request.IsolateInWorktree, project?.Id,
            // AC-1300: the relation is stamped on here, at the one moment the caller is known, rather than read
            // back out of the spawn trail later — a trail records what happened, not what is still true.
            StartedByTheAssistant: request.Target.Caller == SpawnCaller.Assistant)).ConfigureAwait(false);

        if (started is not { } pane)
        {
            // Null means the cockpit has no session factories, the launch declined, or the desk closed before the
            // pane landed — all states the operator can be told about, and none is an exception.
            return await _RefuseSpawnAsync(request, workspace.Name,
                "The cockpit could not start a session just now.", cancellationToken).ConfigureAwait(false);
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
            Refusal: null,
            ProjectId: project?.Id), cancellationToken).ConfigureAwait(false);

        return AgentSpawnResult.Started(pane.PaneId, pane.Name, request.WorkingDirectory, pane.PromptDelivered, resolvedProfileLabel: profile.Label);
    }

    public async Task<AgentStopResult> StopAsync(
        string paneId,
        SpawnCaller caller = SpawnCaller.Assistant,
        string? callerPaneId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _StopAsync(paneId, caller, callerPaneId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Same reason as SpawnAsync: a teardown that threw is a refusal, and a refusal belongs in the record.
            return await _RefuseStopAsync(paneId, $"Closing that session failed: {exception.Message}", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<AgentStopResult> _StopAsync(string paneId, SpawnCaller caller, string? callerPaneId, CancellationToken cancellationToken)
    {
        // First, and by identity rather than by whether it happens to be findable: the assistant is not in the
        // registry's panes, but that is where it sits, not a rule — and a rule is what "the assistant does not end
        // itself mid-sentence" needs to be.
        if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
        {
            return await _RefuseStopAsync(paneId, "That is my own session, and I do not get to end it.", cancellationToken)
                .ConfigureAwait(false);
        }

        var (pane, refusal) = await launcher.RunExclusiveAsync(
            () => _GridAgentPane(paneId, "so I cannot close it. Whoever started it ends it.")).ConfigureAwait(false);
        if (pane is null)
        {
            return await _RefuseStopAsync(paneId, refusal ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await launcher.StopSessionAsync(paneId).ConfigureAwait(false);

        await _RecordAsync(new AssistantSpawnAuditEntry(
            DateTimeOffset.Now,
            AssistantSpawnAction.Stop,
            // Who actually asked, not who used to be the only one who could (AC-795).
            caller,
            callerPaneId,
            pane.WorkspaceId,
            pane.WorkspaceName,
            pane.ProfileLabel,
            WorkingDirectory: null,
            paneId,
            pane.Title,
            Refusal: null), cancellationToken).ConfigureAwait(false);

        return AgentStopResult.Stopped(paneId, pane.Title);
    }

    // The grid pane `paneId` names, as the moment of deciding saw it, or why there is none — the lookup stop, prompt
    // and handover share, so what can be acted on is exactly what the read gateway lists. Runs inside
    // RunExclusiveAsync: the registry also holds embedded panes (an Autopilot step), which only their owner may drive.
    private (GridAgentPane? Pane, string? Refusal) _GridAgentPane(string paneId, string embeddedRefusal)
    {
        if (sessions.Find(paneId) is not { } session)
        {
            return (null, $"There is no session with pane id '{paneId}' — it may already have been closed.");
        }

        if (session.IsEmbedded)
        {
            return (null, $"'{session.Title}' runs inside a workspace's own surface rather than as a pane, {embeddedRefusal}");
        }

        // A plain terminal has a pane id and no agent on the other end.
        if (session.IsTerminal)
        {
            return (null, $"'{session.Title}' is a terminal pane, not an agent session.");
        }

        return (new GridAgentPane(session, session.Title, session.WorkspaceId,
            _FindWorkspace(session.WorkspaceId)?.Name, session.ActiveProfileLabel), null);
    }

    private sealed record GridAgentPane(ISessionHandle Session, string Title, string WorkspaceId, string? WorkspaceName, string? ProfileLabel);

    public async Task<AgentMessageResult> SendMessageAsync(string paneId, string kind, string body, CancellationToken cancellationToken = default)
    {
        try
        {
            // By identity, before anything is looked up, for the same reason StopAsync checks it first: the assistant
            // is not in Sessions today, but that is where it sits and not a rule. A message to itself is a note to
            // nobody — and, if anything ever did read it, a way to put text of its own choosing into its own turn.
            if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
            {
                return AgentMessageResult.Refused("That is my own session. There is nobody on the other end of a message I send myself.");
            }

            // Asked of the addressee's own pane rather than a caller's desk, so this reaches every desk without
            // changing what any other sender may reach. A pane that isn't an agent session (a plain terminal),
            // or no longer exists, resolves to nothing here.
            if (await agents.GetWorkspaceSnapshotAsync(paneId).ConfigureAwait(false) is not { } snapshot
                || snapshot.Panes.FirstOrDefault(pane => string.Equals(pane.PaneId, paneId, StringComparison.Ordinal)) is not { } recipient)
            {
                return await _RefuseMessageAsync(
                    paneId, kind, body, AgentNotifyOutcome.RefusedNotInWorkspace,
                    $"There is no agent session with pane id '{paneId}' that can be written to — it may have closed, or it may be a terminal pane with no agent on the other end.").ConfigureAwait(false);
            }

            var delivery = inbox.Deliver(AssistantIdentity.PaneId, paneId, kind, body);
            if (delivery is not { Message: { } message })
            {
                return await _RefuseMessageAsync(
                    paneId, kind, body, AgentNotifyOutcome.RefusedRecipientInboxFull,
                    $"'{recipient.Name}' has not read its inbox and it is full, so this message was not accepted. Nothing was dropped to make room for it.").ConfigureAwait(false);
            }

            var deduplicated = delivery.Outcome == AgentMessageDeliveryOutcome.Deduplicated;
            await notifyAudit.RecordAsync(new AgentNotifyAuditEntry(
                DateTimeOffset.UtcNow,
                deduplicated ? AgentNotifyOutcome.Deduplicated : AgentNotifyOutcome.Accepted,
                AssistantIdentity.PaneId,
                paneId,
                kind,
                body,
                message.Id), cancellationToken).ConfigureAwait(false);

            return AgentMessageResult.Sent(paneId, recipient.Name, message.Id, deduplicated, recipient.DeliversAtTurnStart);
        }
        catch (Exception exception)
        {
            return await _RefuseMessageAsync(paneId, kind, body, AgentNotifyOutcome.RefusedError, exception.Message).ConfigureAwait(false);
        }
    }

    // Records the refusal on the same trail an agent's own refused `notify` lands on, then reports it.
    private async Task<AgentMessageResult> _RefuseMessageAsync(
        string paneId, string kind, string body, AgentNotifyOutcome outcome, string reason)
    {
        await notifyAudit.RecordAsync(new AgentNotifyAuditEntry(
            DateTimeOffset.UtcNow, outcome, AssistantIdentity.PaneId, paneId, kind, body, MessageId: null)).ConfigureAwait(false);
        return AgentMessageResult.Refused(reason);
    }

    public async Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default)
    {
        // The same three refusals as StopAsync, in the same order and for the same reasons — see the comments
        // there. A pane the assistant may not end is a pane it may not speak as either.
        if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
        {
            return await _RefusePromptAsync(paneId, "That is my own session, and I do not get to hand myself a turn.", cancellationToken).ConfigureAwait(false);
        }

        var (pane, refusal) = await launcher.RunExclusiveAsync(
            () => _GridAgentPane(paneId, "so I cannot hand it a turn. Whoever started it drives it.")).ConfigureAwait(false);
        if (pane is null)
        {
            return await _RefusePromptAsync(paneId, refusal ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        // Held rather than dropped when the session is still coming up, and the caller is told which of the two
        // happened. Null when a pane still coming up already holds its one brief: a second one arriving first would
        // otherwise be misread as belonging to this call. Told plainly, so it doesn't read as an invitation to retry.
        if (await pane.Session.SubmitPromptWhenReadyAsync(prompt).ConfigureAwait(false) is not { } delivered)
        {
            return await _RefusePromptAsync(
                paneId,
                $"'{pane.Title}' is still starting and already has a turn waiting. It gets the one it was given first; this one was not accepted.",
                cancellationToken).ConfigureAwait(false);
        }

        await _RecordAsync(new AssistantSpawnAuditEntry(
            DateTimeOffset.Now,
            AssistantSpawnAction.Prompt,
            SpawnCaller.Assistant,
            CallerPaneId: null,
            pane.WorkspaceId,
            pane.WorkspaceName,
            pane.ProfileLabel,
            WorkingDirectory: null,
            paneId,
            pane.Title,
            Refusal: null), cancellationToken).ConfigureAwait(false);

        return AgentPromptResult.Handed(paneId, pane.Title, delivered);
    }

    private async Task<AgentPromptResult> _RefusePromptAsync(string paneId, string reason, CancellationToken cancellationToken)
    {
        await _RecordAsync(new AssistantSpawnAuditEntry(
            DateTimeOffset.Now,
            AssistantSpawnAction.Prompt,
            SpawnCaller.Assistant,
            CallerPaneId: null,
            WorkspaceId: string.Empty,
            WorkspaceName: null,
            Profile: null,
            WorkingDirectory: null,
            paneId,
            SessionName: null,
            reason), cancellationToken).ConfigureAwait(false);

        return AgentPromptResult.Refused(reason);
    }

    // Not on the spawn trail, and deliberately: that record exists because a spawn starts a process and spends
    // money. A rename costs nothing, is reversible, and shows up on the operator's own screen as it happens.
    public async Task<AssistantRenameResult> RenameSessionAsync(string paneId, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return AssistantRenameResult.Refused("A session needs a name; that one was empty.");
        }

        // By identity rather than by whether it happens to be findable — the same rule, and the same reason, as
        // _StopAsync: the assistant sits outside the registry's panes, but that is where it sits and not a rule.
        if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
        {
            return AssistantRenameResult.Refused("That is my own session, and I do not get to name it.");
        }

        if (await launcher.SetSessionNameAsync(paneId, trimmed).ConfigureAwait(false))
        {
            return AssistantRenameResult.Renamed(trimmed);
        }

        // SetSessionNameAsync reaches the grid only, so false past the guards above means the pane is not one the
        // grid holds. The registry separates the two cases the assistant can actually be looking at, because it
        // lists embedded panes and would otherwise be told they had closed.
        return AssistantRenameResult.Refused(sessions.Find(paneId) is { } elsewhere
            ? $"'{elsewhere.Title}' runs inside a workspace's own surface rather than as a pane, so I cannot rename it."
            : $"There is no session with pane id '{paneId}' — it may already have been closed.");
    }

    public async Task<AssistantRenameResult> RenameWorkspaceAsync(string workspaceId, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return AssistantRenameResult.Refused("A workspace needs a name; that one was empty.");
        }

        // Looked up here rather than left to the rename itself, which returns silently for a desk it cannot
        // find — and a silent no-op would come back to the assistant as a rename that happened.
        if (await launcher.RunExclusiveAsync(() => _FindWorkspace(workspaceId)).ConfigureAwait(false) is not { } workspace)
        {
            return AssistantRenameResult.Refused(
                $"There is no workspace with id '{workspaceId}'. List the workspaces and name one of those.");
        }

        await launcher.RenameWorkspaceAsync(workspace.Id, trimmed).ConfigureAwait(false);
        return AssistantRenameResult.Renamed(trimmed);
    }

    public Task<IReadOnlyList<AssistantWorkspaceRow>> ListWorkspacesAsync(CancellationToken cancellationToken = default) =>
        launcher.RunExclusiveAsync(_ListWorkspaces);

    // AC-647/AC-649: profiles straight off the store (no UI thread — this reads a file, not the cockpit's
    // collections). Each profile's config is reported via the provider's own declared schema, so Claude's
    // permission mode/model/effort and Codex's sandbox arrive in their own vocabulary, not a generic dump.
    public async Task<IReadOnlyList<AssistantProfileRow>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        var known = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. known.Select(profile =>
            {
                var registration = profile.ProviderConfig is PluginProviderConfig plugin
                    ? pluginProviders.Resolve(plugin.ProviderId)
                    : null;

                return new AssistantProfileRow(
                    profile.Label,
                    // The plugin's own name ("Claude", "Codex"), not the bare `Plugin` enum value: every plugin-backed
                    // profile reads as the same provider otherwise, and this is the field the tool tells the assistant
                    // to resolve "a Claude one" by.
                    registration?.DisplayName ?? profile.Provider.ToString(),
                    ProfileModel.Of(profile))
                {
                    Options = ProfileOptionReport.For(registration?.Capabilities, profile.Defaults?.OptionDefaults),
                };
            }),
        ];
    }

    public async Task<AssistantWorkspaceRow?> CreateWorkspaceAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var created = await launcher.CreateSessionsWorkspaceAsync(trimmed).ConfigureAwait(false);
        return new AssistantWorkspaceRow(
            created.Id, created.Name, created.Type.Id, CanHostSessions: true, SessionCount: 0, IsActive: true);
    }

    // AC-1013: closes an empty sessions desk only (narrower than the tab's ✕, which closes the desk and
    // everything on it behind a dialog naming what's lost). No dialog here, so sessions must be stopped first via
    // `stop_agent`; non-sessions desks are refused wholesale since a consent card can't enumerate their contents.
    public async Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var (workspace, refusal) = await launcher.RunExclusiveAsync(() => _DecideRemoval(workspaceId)).ConfigureAwait(false);
        if (workspace is null)
        {
            return WorkspaceRemovalResult.Refused(refusal ?? string.Empty);
        }

        // Counted and closed in one step, so a session landing on the desk after the decision above is counted
        // rather than closed along with it.
        var occupants = await launcher.CloseWorkspaceIfEmptyAsync(workspaceId).ConfigureAwait(false);
        if (occupants > 0)
        {
            return WorkspaceRemovalResult.Refused(occupants == 1
                ? $"There is still 1 session on '{workspace.Name}'. Stop it first — I do not close a desk with work still on it."
                : $"There are still {occupants} sessions on '{workspace.Name}'. Stop them first — I do not close a desk with work still on it.");
        }

        return WorkspaceRemovalResult.Removed(workspace.Name);
    }

    private (Workspace? Workspace, string? Refusal) _DecideRemoval(string workspaceId)
    {
        if (_FindWorkspace(workspaceId) is not { } workspace)
        {
            return (null, $"There is no workspace with id '{workspaceId}'. List the workspaces and name one of those.");
        }

        // The button's own gate, asked rather than re-derived — CanClose is what greys out the ✕, and the two
        // reasons it says no for are worth telling apart out loud.
        if (!launcher.CanCloseWorkspace(workspaceId))
        {
            return (null, workspace.Type == WorkspaceType.Projects
                ? $"'{workspace.Name}' is the projects overview. It is always there, and closing it is not something anyone can do."
                : $"'{workspace.Name}' is the only desk left, and the cockpit always needs one to show.");
        }

        // Before the session count, because that count is about sessions and a desk of another type has none —
        // it would read as empty and the close would go through.
        if (workspace.Type != WorkspaceType.Sessions)
        {
            return (null, $"'{workspace.Name}' is not a sessions desk — it is a {workspace.Type.Id} desk, and this tool only closes the ones that hold sessions. What is on it is not sessions I can count or stop, so closing it is the operator's own to do from its tab. Nothing is lost by asking them.");
        }

        return (workspace, null);
    }

    private IReadOnlyList<AssistantWorkspaceRow> _ListWorkspaces()
    {
        var settings = launcher.Workspaces;

        // AC-543: counted via the same placement rule the read path uses, not each session's own stamp, so this
        // roster never disagrees with the sidebar. The assistant is excluded — it is the one asking.
        var counts = sessions.All
            .Where(session => !session.IsTerminal
                && !string.Equals(session.PaneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
            .Select(session => session.PlacedWorkspaceId)
            .OfType<string>()
            .GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return
        [
            .. settings.Workspaces.Select(workspace => new AssistantWorkspaceRow(
                workspace.Id,
                workspace.Name,
                // The id, not ToString(): WorkspaceType is a record struct, so ToString() hands the model
                // "WorkspaceType { Id = Sessions, IsBuiltIn = True }" — a record dump where the row's own contract
                // says "sessions". Found in a live transcript (Raymond, 2026-08-02).
                workspace.Type.Id,
                workspace.Type == WorkspaceType.Sessions,
                counts.TryGetValue(workspace.Id, out var count) ? count : 0,
                string.Equals(workspace.Id, settings.Active?.Id, StringComparison.Ordinal))),
        ];
    }

    // The route asked for, or null for "whatever the profile is set to". A word that is neither is refused rather
    // than read as the default: the operator said a route out loud, and starting the other one would look like it
    // worked. "cli" and "terminal" are accepted for tty because those are the words people actually say.
    private static (PaneSessionKind? Kind, string? Refusal) _ParseKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            null or "" => (null, null),
            "sdk" => (PaneSessionKind.Sdk, null),
            "tty" or "cli" or "terminal" => (PaneSessionKind.Tty, null),
            var other => (null, $"'{other}' is not a route I know — it is either sdk or tty."),
        };

    private Workspace? _FindWorkspace(string? workspaceId) =>
        workspaceId is null
            ? null
            : launcher.Workspaces.Workspaces.FirstOrDefault(
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
            reason,
            ProjectId: request.ProjectId), cancellationToken).ConfigureAwait(false);

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
            reason), cancellationToken).ConfigureAwait(false);

        return AgentStopResult.Refused(reason);
    }

    // AC-640: the watcher decides everything — whether the pane resolves, whether it keeps a transcript, whether the
    // pattern compiles — because it is the half that has to live with the answer every tick.
    public Task<AssistantWatchResult> WatchSessionAsync(
        string paneId,
        IReadOnlyList<string>? events,
        int? afterMinutes = null,
        string? pattern = null,
        CancellationToken cancellationToken = default) =>
        watcher.WatchAsync(paneId, events, afterMinutes, pattern);

    public Task<bool> UnwatchSessionAsync(string paneId, CancellationToken cancellationToken = default) =>
        watcher.UnwatchAsync(paneId);

    // AC-719 ronde B: re-owns a worktree the assistant made for itself onto a running session, via the same
    // ReattachAsync primitive the reattach guard in _ResolveIsolatedWorkingDirectoryAsync uses. Every refusal here
    // is hard, not best-effort — a wrong target could pull a worktree out from under a session that needed it.
    public async Task<WorktreeHandoverResult> HandoverWorktreeAsync(string path, string paneId, CancellationToken cancellationToken = default)
    {
        if (worktreeManager is null)
        {
            return await _RefuseHandoverAsync(path, paneId, "Worktree management is not available here.", cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
        {
            return await _RefuseHandoverAsync(path, paneId, "That is my own session; a worktree cannot be handed to it.", cancellationToken).ConfigureAwait(false);
        }

        var full = Path.GetFullPath(path);
        var record = (await worktreeManager.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(Path.GetFullPath(candidate.Path), full, _PathComparison));
        if (record is null)
        {
            return await _RefuseHandoverAsync(path, paneId, "No managed worktree at that path — call worktree_list for the current paths.", cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(record.SessionId, AssistantIdentity.PaneId, StringComparison.Ordinal))
        {
            return await _RefuseHandoverAsync(record.Path, paneId, "That worktree is not mine to hand over — it belongs to a different session.", cancellationToken).ConfigureAwait(false);
        }

        var (pane, refusal) = await launcher.RunExclusiveAsync(
            () => _GridAgentPane(paneId, "so a worktree cannot be handed to it.")).ConfigureAwait(false);
        if (pane is null)
        {
            return await _RefuseHandoverAsync(record.Path, paneId, refusal ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        WorktreeRecord? reattached;
        try
        {
            reattached = await worktreeManager.TransferAsync(record.Path, AssistantIdentity.PaneId, paneId, cancellationToken).ConfigureAwait(false);
        }
        catch (WorktreeAdmissionException exception)
        {
            return await _RefuseHandoverAsync(record.Path, paneId, exception.Message, cancellationToken).ConfigureAwait(false);
        }

        if (reattached is null)
        {
            return await _RefuseHandoverAsync(record.Path, paneId, "The worktree could not be re-owned — it may have just been removed.", cancellationToken)
                .ConfigureAwait(false);
        }

        await pane.Session.SetWorktreeBranchAsync(reattached.Branch).ConfigureAwait(false);

        await _RecordAsync(new AssistantSpawnAuditEntry(
            DateTimeOffset.Now,
            AssistantSpawnAction.Handover,
            SpawnCaller.Assistant,
            CallerPaneId: null,
            pane.WorkspaceId,
            pane.WorkspaceName,
            Profile: null,
            WorkingDirectory: reattached.Path,
            paneId,
            pane.Title,
            Refusal: null), cancellationToken).ConfigureAwait(false);

        return WorktreeHandoverResult.HandedOver(reattached.Path, reattached.Branch, pane.Title);
    }

    // AC-587: the assistant's own door onto the app's one link opener (`ExternalLinkSingleSourceTests`), reached
    // through `IExternalLinkOpener` since this class left the app (AC-1375).
    public Task<OpenUrlResult> OpenUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!links.TryParseWebAddress(url, out var address))
        {
            return Task.FromResult(OpenUrlResult.Refused(
                $"'{url}' is not an absolute http(s) address, so there is nothing to open."));
        }

        return Task.FromResult(links.TryOpen(address)
            ? OpenUrlResult.Opened(address.AbsoluteUri)
            : OpenUrlResult.Refused($"The browser would not open '{address.AbsoluteUri}'."));
    }

    // AC-1261 criterion 7 (V4): the assistant's own door onto `AssistantSessionHost.RequestConversationClear` —
    // exists for the same reason `OpenUrlAsync` does.
    public Task<ClearConversationResult> RequestConversationClearAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ClearConversationResult.Requested(alreadyQueued: !conversation.RequestConversationClear()));

    private async Task<WorktreeHandoverResult> _RefuseHandoverAsync(
        string path, string paneId, string reason, CancellationToken cancellationToken)
    {
        await _RecordAsync(new AssistantSpawnAuditEntry(
            DateTimeOffset.Now,
            AssistantSpawnAction.Handover,
            SpawnCaller.Assistant,
            CallerPaneId: null,
            WorkspaceId: string.Empty,
            WorkspaceName: null,
            Profile: null,
            WorkingDirectory: path,
            paneId,
            SessionName: null,
            reason), cancellationToken).ConfigureAwait(false);

        return WorktreeHandoverResult.Refused(reason);
    }

    // AC-798: the "Add to my projects…" dialog route minus the window, reusing the dialog's own composition and
    // persisting so `cockpit.json` matches what clicking would have produced. Folder/profile/resource reference
    // are never invented — each missing one is refused with a question for the operator; does not clone (folder must already exist).
    public async Task<AssistantProjectBindResult> BindSharedProjectAsync(
        string sharedProjectId,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences = null,
        CancellationToken cancellationToken = default)
    {
        var (sources, boundIds, hiddenIds) = await projectEditor.ReadSharedProjectSourcesAsync().ConfigureAwait(false);

        if (sources.Count == 0)
        {
            return AssistantProjectBindResult.Refused(
                "No connection on this machine offers shared projects, so there is nothing to add from.");
        }

        var id = sharedProjectId?.Trim() ?? string.Empty;

        // The same `{scheme}:{slug}` prefix rule `ProjectsViewModel.FinishSettingUpAsync` resolves a row's source
        // with — the id says which connection it came from, so nothing has to be carried alongside it.
        var source = sources.FirstOrDefault(
            candidate => id.StartsWith(candidate.Key + ":", StringComparison.Ordinal));
        if (source is null)
        {
            return AssistantProjectBindResult.Refused(
                $"No connection here offers a project with id '{id}'. Call list_shared_projects and name one of the ids it reports.");
        }

        // Already bound is refused rather than bound again: the dialog is protected by the row disappearing off the
        // list, and this door has no list to disappear from. Two local projects on one shared definition is not an
        // untidiness — it is two projects whose write-back would fight over the same remote definition.
        if (boundIds.Contains(id))
        {
            return AssistantProjectBindResult.Refused(
                $"'{id}' is already added on this machine; call list_projects to find it. Adding it a second time would make two local projects out of one shared one.");
        }

        // Hidden here is the operator saying they do not want this one offered, and the Projects page honours that by
        // having no card to click. A door that binds it anyway would be the one way past a choice they made — and
        // `list_shared_projects` already leaves it out, so an id that reaches here was not read off any list.
        if (hiddenIds.Contains(id))
        {
            return AssistantProjectBindResult.Refused(
                $"'{id}' is hidden on this machine, so it is not on offer here. If the operator wants it after all, they unhide it on the Projects page themselves.");
        }

        var directory = sourceDirectory?.Trim();
        if (string.IsNullOrEmpty(directory))
        {
            return AssistantProjectBindResult.Refused(
                "Which folder on this machine holds this project? It is not part of what is shared, and this tool does not clone one — ask the operator for a full path that already exists.");
        }

        // A relative path resolves against whatever directory the cockpit process happens to have been started in,
        // which is nobody's answer to "where does this project live" — and it would go on the consent card as a
        // folder the operator cannot check.
        if (!Path.IsPathFullyQualified(directory))
        {
            return AssistantProjectBindResult.Refused(
                $"'{directory}' is a relative path, and you are standing in no directory — ask the operator for the full path.");
        }

        if (!Directory.Exists(directory))
        {
            return AssistantProjectBindResult.Refused(
                $"There is no folder at '{directory}'. This tool does not clone, so the folder has to be there already — ask the operator where the project lives, or to clone it first.");
        }

        if (string.IsNullOrWhiteSpace(profileLabel))
        {
            return AssistantProjectBindResult.Refused(
                "Which profile should this project's sessions run under? A shared project carries no profile, and it is the one field this step requires — call list_profiles and ask the operator which of those.");
        }

        // Read off the UI thread, like every other profile lookup here, and matched the same way `SpawnAsync` does:
        // by label, case-insensitively, never "the first one that looks close".
        var known = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = known.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, profileLabel.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            var labels = known.Count == 0 ? "none are configured" : string.Join(", ", known.Select(candidate => $"'{candidate.Label}'"));
            return AssistantProjectBindResult.Refused(
                $"There is no profile called '{profileLabel}'. The profiles this cockpit knows are: {labels}.");
        }

        var (composed, composeRefusal) = await projectEditor
            .ComposeSharedProjectAsync(id, source, directory, profile.Label, resourceReferences, cancellationToken).ConfigureAwait(false);
        if (composed is null)
        {
            return AssistantProjectBindResult.Refused(composeRefusal ?? "Could not read this project's definition.");
        }

        var stored = await projectEditor.AddBoundProjectAsync(composed).ConfigureAwait(false);

        return AssistantProjectBindResult.Bound(stored.Id, stored.Name, source.SourceName, stored.SourceDirectory);
    }

    // AC-799: "New project" without the window, refused by the editor's own `CanSave`/`ToProject` rather than a
    // second copy of that rule — checked against what Depot already shares before anything local is written, and
    // `sourceDirectory`/`pluginFields` get a scrutiny the dialog's own free-text boxes do not give them.
    public async Task<AssistantProjectCreateResult> CreateProjectAsync(
        string name,
        string? description = null,
        string? sourceDirectory = null,
        string? defaultProfileLabel = null,
        string? behaviorPrompt = null,
        bool isolateInWorktreeByDefault = false,
        IReadOnlyList<string>? enabledMcpServerNames = null,
        string? category = null,
        IReadOnlyDictionary<string, string>? pluginFields = null,
        CancellationToken cancellationToken = default)
    {
        // AC-799 review finding 10: the cheap, purely local checks first, and the network round trip
        // (`_FindSharedProjectByNameAsync`, one call per configured source) last — a typo'd folder or an unknown
        // plugin-field key should not wait on a colleague's server before being reported.
        if (_RefuseUnlessValidOptionalDirectory(sourceDirectory) is { } directoryError)
        {
            return AssistantProjectCreateResult.Refused(directoryError);
        }

        if (_RefuseUnknownPluginFieldKeys(pluginFields) is { } unknownFieldError)
        {
            return AssistantProjectCreateResult.Refused(unknownFieldError);
        }

        // AC-799 review finding 3: validated by label against `list_profiles`, same rule `SpawnAsync` and
        // `BindSharedProjectAsync` hold `profile` to. Needed because `SelectedProfileLabel` carries no guard of
        // its own — normally a human can only pick from a bound combo box, a guarantee lost when set directly.
        if (await _RefuseIfUnknownProfileLabelAsync(defaultProfileLabel, cancellationToken).ConfigureAwait(false) is { } unknownProfileError)
        {
            return AssistantProjectCreateResult.Refused(unknownProfileError);
        }

        var (project, composeRefusal) = await projectEditor.ComposeNewProjectAsync(
            name, description, sourceDirectory, behaviorPrompt, isolateInWorktreeByDefault, category, defaultProfileLabel,
            cancellationToken).ConfigureAwait(false);

        if (composeRefusal is not null)
        {
            return AssistantProjectCreateResult.Refused(composeRefusal);
        }

        if (project is null)
        {
            return AssistantProjectCreateResult.Refused("A project needs a name.");
        }

        if (await _FindSharedProjectByNameAsync(project.Name, cancellationToken).ConfigureAwait(false) is { } collision)
        {
            return AssistantProjectCreateResult.Refused(
                $"'{collision.Name}' is already shared via {collision.SourceName} (id '{collision.Id}'). Call "
                + "bind_shared_project with that id if it is the same project, rather than creating a second, "
                + "disconnected local project under the same name.");
        }

        var withDynamicFields = project with
        {
            McpOverlay = new ProjectMcpOverlay { EnabledServerNames = enabledMcpServerNames },
            PluginFields = pluginFields ?? ReadOnlyDictionary<string, string>.Empty,
        };

        var stored = await projectEditor.AddNewProjectAsync(withDynamicFields).ConfigureAwait(false);
        return AssistantProjectCreateResult.Created(stored.Id, stored.Name);
    }

    // AC-1059: read side of `UpdateProjectAsync`, so the MCP tool can build a before/after card without this
    // gateway ever raising consent itself (that stays the caller's job, same split every other tool here keeps).
    public async Task<AssistantProjectSnapshot?> GetProjectSnapshotAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var project = await projectEditor.FindProjectAsync(projectId).ConfigureAwait(false);
        return project is null
            ? null
            : new AssistantProjectSnapshot(
                project.Id,
                project.Name,
                project.Description,
                project.SourceDirectory,
                project.DefaultProfileLabel,
                project.BehaviorPrompt,
                project.IsolateInWorktreeByDefault,
                project.McpOverlay.EnabledServerNames,
                project.Category,
                project.PluginFields,
                project.GitUrl,
                project.MemoryRef);
    }

    // AC-1059: patches only the named fields onto the stored project, never `ProjectDialogViewModel.ToProject()`'s
    // full rebuild — the shape that silently dropped `Resources` (AC-483) and still drops `LastOpenedAt` today.
    public async Task<AssistantProjectUpdateResult> UpdateProjectAsync(
        string projectId,
        string? name = null,
        string? description = null,
        string? sourceDirectory = null,
        string? defaultProfileLabel = null,
        string? behaviorPrompt = null,
        bool? isolateInWorktreeByDefault = null,
        IReadOnlyList<string>? enabledMcpServerNames = null,
        string? category = null,
        IReadOnlyDictionary<string, string>? pluginFields = null,
        string? gitUrl = null,
        string? memoryRef = null,
        CancellationToken cancellationToken = default)
    {
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            return AssistantProjectUpdateResult.Refused("A project needs a name; leave `name` out to keep its current one.");
        }

        if (_RefuseUnlessValidOptionalDirectory(sourceDirectory) is { } directoryError)
        {
            return AssistantProjectUpdateResult.Refused(directoryError);
        }

        if (_RefuseUnknownPluginFieldKeys(pluginFields) is { } unknownFieldError)
        {
            return AssistantProjectUpdateResult.Refused(unknownFieldError);
        }

        if (await _RefuseIfUnknownProfileLabelAsync(defaultProfileLabel, cancellationToken).ConfigureAwait(false) is { } unknownProfileError)
        {
            return AssistantProjectUpdateResult.Refused(unknownProfileError);
        }

        var stored = await projectEditor.FindProjectAsync(projectId).ConfigureAwait(false);
        if (stored is null)
        {
            return AssistantProjectUpdateResult.Refused($"There is no project with id '{projectId}'. Call list_projects to see what exists.");
        }

        var updated = stored;

        if (name is not null)
        {
            updated = updated with { Name = name.Trim() };
        }

        if (description is not null)
        {
            updated = updated with { Description = description.Length == 0 ? null : description };
        }

        // Only item 0 changes (AC-938: a project can carry more than one repository) — the rest of
        // `SourceDirectories` is carried through untouched, the same restriction `CreateProjectAsync`'s own doc
        // string states for the folder it sets.
        if (sourceDirectory is not null)
        {
            var repositories = updated.SourceDirectories;
            updated = updated with
            {
                SourceDirectories = sourceDirectory.Length == 0
                    ? [.. repositories.Skip(1)]
                    : [
                        repositories.Count > 0 ? repositories[0] with { Path = sourceDirectory } : new ProjectRepository(sourceDirectory),
                        .. repositories.Skip(1),
                    ],
            };
        }

        if (defaultProfileLabel is not null)
        {
            updated = updated with { DefaultProfileLabel = defaultProfileLabel.Length == 0 ? null : defaultProfileLabel.Trim() };
        }

        if (behaviorPrompt is not null)
        {
            updated = updated with { BehaviorPrompt = behaviorPrompt.Length == 0 ? null : behaviorPrompt };
        }

        if (isolateInWorktreeByDefault is { } isolate)
        {
            updated = updated with { IsolateInWorktreeByDefault = isolate };
        }

        // Same `[]`-means-"every server" collapse `CreateProjectAsync` applies (AC-799 review finding 1), so the
        // two tools never disagree about what an explicit empty list means.
        if (enabledMcpServerNames is not null)
        {
            updated = updated with
            {
                McpOverlay = updated.McpOverlay with { EnabledServerNames = enabledMcpServerNames.Count == 0 ? null : enabledMcpServerNames },
            };
        }

        if (category is not null)
        {
            updated = updated with { Category = category.Length == 0 ? null : category };
        }

        if (gitUrl is not null)
        {
            updated = updated with { GitUrl = gitUrl.Length == 0 ? null : gitUrl };
        }

        // `MemoryRef`'s own setter already treats a blank value as "remove every Memory row" (Project.cs), so an
        // empty string clears it without a ternary here doing that same job twice.
        if (memoryRef is not null)
        {
            updated = updated with { MemoryRef = memoryRef };
        }

        // Upserts by key rather than replacing the whole map: naming one plugin field must not drop a sibling one
        // the caller never mentioned — the same partial-update contract this whole method carries, one level deeper.
        if (pluginFields is not null)
        {
            var merged = new Dictionary<string, string>(updated.PluginFields, StringComparer.Ordinal);
            foreach (var (key, value) in pluginFields)
            {
                merged[key] = value;
            }

            updated = updated with { PluginFields = merged };
        }

        var result = await projectEditor.UpdateStoredProjectAsync(updated).ConfigureAwait(false);
        return result is null
            ? AssistantProjectUpdateResult.Refused($"There is no project with id '{projectId}'. Call list_projects to see what exists.")
            : AssistantProjectUpdateResult.Updated(result.Id, result.Name);
    }

    // AC-799 finding 3 / AC-1059: shared by `CreateProjectAsync` and `UpdateProjectAsync` — a label matched
    // against `list_profiles`, since neither view-model property carries a guard of its own. Null when
    // `profileLabel` is blank: leaving it out, or clearing it, needs no profile to exist.
    private async Task<string?> _RefuseIfUnknownProfileLabelAsync(string? profileLabel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileLabel))
        {
            return null;
        }

        var known = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (known.Any(candidate => string.Equals(candidate.Label, profileLabel.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var labels = known.Count == 0 ? "none are configured" : string.Join(", ", known.Select(candidate => $"'{candidate.Label}'"));
        return $"There is no profile called '{profileLabel}'. The profiles this cockpit knows are: {labels}.";
    }

    // The same registry and per-source timeout `list_shared_projects` itself reads through
    // (`SharedProjectSourceLister.ListWithTimeoutAsync`), not a second copy of either — a source that is slow, signed
    // out or unreachable is skipped rather than failing the whole check. Null when nothing matches.
    private async Task<(string SourceName, string Id, string Name)?> _FindSharedProjectByNameAsync(
        string name, CancellationToken cancellationToken)
    {
        var (sources, boundIds, hiddenIds) = await projectEditor.ReadSharedProjectSourcesAsync().ConfigureAwait(false);

        if (sources.Count == 0)
        {
            return null;
        }

        var results = await Task.WhenAll(sources.Select(source => SharedProjectSourceLister.ListWithTimeoutAsync(source, cancellationToken)))
            .ConfigureAwait(false);

        foreach (var (source, result) in sources.Zip(results))
        {
            if (!result.Succeeded)
            {
                continue;
            }

            var match = result.Projects.FirstOrDefault(shared =>
                !boundIds.Contains(shared.Id) && !hiddenIds.Contains(shared.Id)
                && string.Equals(shared.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return (source.SourceName, match.Id, match.Name);
            }
        }

        return null;
    }

    // Full path and already there — the same two refusals `BindSharedProjectAsync` gives an assistant-supplied
    // folder, because a typo here is a session pointed at the wrong place. Null when `directory` is blank: an
    // administrative project with no folder of its own is a perfectly good project.
    private static string? _RefuseUnlessValidOptionalDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            return $"'{directory}' is a relative path, and you are standing in no directory — ask the operator for "
                + "the full path, or leave sourceDirectory out for a project with no folder of its own.";
        }

        if (!Directory.Exists(directory))
        {
            return $"There is no folder at '{directory}'. Ask the operator where the project lives, or leave "
                + "sourceDirectory out for a project with no folder of its own.";
        }

        return null;
    }

    // Keys come from the same dynamic registry the project editor's own plugin-fields section draws its rows
    // from (`ProjectFieldRegistry`) — a hard-coded list on this tool would go stale the moment a plugin is
    // installed or removed, which is exactly what that registry exists to avoid.
    private string? _RefuseUnknownPluginFieldKeys(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is not { Count: > 0 })
        {
            return null;
        }

        var known = (projectFields?.Fields ?? []).Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = fields.Keys.Where(key => !known.Contains(key)).ToList();
        if (unknown.Count == 0)
        {
            return null;
        }

        var knownList = known.Count == 0 ? "none are registered" : string.Join(", ", known.Select(key => $"'{key}'"));
        return $"'{string.Join("', '", unknown)}' is not a plugin field this cockpit knows. The registered keys are: {knownList}.";
    }

    // AC-955: reaches the assistant's own session through `IAssistantConversation`, not the registry (the assistant
    // sits outside its panes by design — see `StopAsync`'s remark). No audit trail, unlike a spawn: showing a
    // question costs nothing and starts nothing.
    public async Task<AskStructuredQuestionResult> AskStructuredQuestionAsync(
        string question,
        IReadOnlyList<(string Label, string? Description)> options,
        bool multiSelect,
        bool allowOther,
        string? header,
        CancellationToken cancellationToken = default)
    {
        var inputJson = _BuildAskStructuredQuestionInputJson(question, options, multiSelect, allowOther, header);
        return await conversation.ShowQuestionAsync(question, inputJson).ConfigureAwait(false)
            ? AskStructuredQuestionResult.Shown()
            : AskStructuredQuestionResult.Refused("My own session is not running, so there is nowhere to show this card.");
    }

    // A mirror of the native AskUserQuestion tool's own `questions` array (AC-715), one entry, so
    // `AskUserQuestionViewModel.Parse` reads it unchanged rather than needing a second parser.
    private static string _BuildAskStructuredQuestionInputJson(
        string question, IReadOnlyList<(string Label, string? Description)> options, bool multiSelect, bool allowOther, string? header)
    {
        var optionsArray = new JsonArray([.. options.Select(option => (JsonNode)new JsonObject
        {
            ["label"] = option.Label,
            ["description"] = option.Description,
        })]);

        var questionObject = new JsonObject
        {
            ["question"] = question,
            ["header"] = header,
            ["multiSelect"] = multiSelect,
            ["allowOther"] = allowOther,
            ["options"] = optionsArray,
        };

        return new JsonObject { ["questions"] = new JsonArray(questionObject) }.ToJsonString();
    }

    private Task _RecordAsync(AssistantSpawnAuditEntry entry, CancellationToken cancellationToken) =>
        auditLog.RecordAsync(entry, cancellationToken);

    // AC-1324: the controller operator's click on a row this cockpit is stopped on — the same path as a click in
    // the pane itself. Not the assistant's own session: nobody answers that one but its operator, here.
    // Only an SDK session's handle answers by tool-use id; a TTY session's prompts are its CLI's own.
    public Task<bool> RespondToPermissionAsync(string paneId, string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        !string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal) && sessions.Find(paneId) is { } session
            ? session.RespondToPermissionByIdAsync(toolUseId, allow)
            : Task.FromResult(false);
}
