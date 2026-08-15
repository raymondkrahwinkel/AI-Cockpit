using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.Services;

// The app-level half of `IAssistantAgentGateway` (AC-545): the one place a session is started or
// stopped on an agent's behalf, and the place every such request is written down.
// Sibling of `AssistantReadGateway` and shaped like it on purpose — same UI-thread marshalling, for the
// same reason: `CockpitViewModel.Sessions` and the workspace settings only ever mutate on the UI thread, and an
// MCP tool call arrives on a Kestrel request thread.
//
// *This class refuses; it does not scope.* The distinction matters and it is not word-play. Which desk a spawn
// may land on was decided before this is reached, by whichever of `SpawnTarget`'s two doors the caller
// came through — that is the guardrail. What happens here is the far duller check that the named desk exists and can
// hold a session at all, which is true of every caller and protects nobody from anything. Do not let the second grow
// into the first: a coordinator (AC-436) must arrive with a target derived from its own pane, never with one this
// class validated on its behalf.
//
// *Every outcome is recorded, including the refusals.* Criterion 5 asks for the trail; a trail that only holds
// what got through would show the gate's successes and hide it working. A failure to write the trail never fails the
// operator's approved action — see `IAssistantSpawnAuditLog`.
//
// *What this is not.* It is not the consent gate. Nothing here decides whether the operator agreed: the
// assistant's session runs in "Ask permissions", so the tool call that reaches this class has already raised an
// Allow/Deny row in the chat window and been clicked. If a future caller could reach these methods without that,
// this class would still start the session — which is why the gate belongs where it is and must not be re-implemented
// here as a second, weaker copy.
internal sealed class AssistantAgentGateway(
    CockpitViewModel cockpit,
    ISessionProfileStore profiles,
    IAssistantSpawnAuditLog auditLog,
    IWorkspaceAgentGateway agents,
    IAgentMessageInbox inbox,
    IAgentNotifyAuditLog notifyAudit,
    IPluginProviderRegistry pluginProviders,
    SessionWatcher watcher,
    IWorktreeManager? worktreeManager = null,
    ISharedProjectSourceRegistry? sharedProjectSources = null,
    IMcpServerCatalog? mcpServerCatalog = null,
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

            // AC-773: the project named by id, if any — looked up the one place a project id becomes a `Project`
            // (`CockpitViewModel.FindProjectByIdAsync`), never re-derived here. An id that names nothing is refused
            // before anything else is checked, same as an unknown workspace id above: a caller that named a project
            // gets told the id was wrong rather than silently falling back to a folder guess.
            Project? project = null;
            if (request.ProjectId is { Length: > 0 } requestedProjectId)
            {
                project = await cockpit.FindProjectByIdAsync(requestedProjectId).ConfigureAwait(true);
                if (project is null)
                {
                    return await _RefuseSpawnAsync(request, workspace.Name,
                        $"There is no project with id '{requestedProjectId}'. Call list_projects and name one of those.",
                        cancellationToken).ConfigureAwait(true);
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
                    cancellationToken).ConfigureAwait(true);
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

            // Checked before anything starts, and against the provider's own declaration rather than a list kept here
            // (AC-648/AC-649): a key this provider never heard of is refused with a reason instead of reaching the CLI
            // as a flag it does not take. `permission-mode` is refused whatever the provider says — see
            // `SpawnOptionOverrides.NeverOverridable`.
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
                return await _RefuseSpawnAsync(request, workspace.Name, optionRefusal, cancellationToken).ConfigureAwait(true);
            }

            // AC-719: refused categorically, like permission-mode — a caller that could dial isolation down per
            // spawn is one hop from the working-tree contamination isolation exists to prevent.
            if (request.IsolateInWorktree == false)
            {
                return await _RefuseSpawnAsync(request, workspace.Name,
                    "'isolate: false' is not something a spawn may ask for — that would run it in the operator's real "
                    + "checkout. Leave it out to use the project's own isolation setting, or ask for isolate: true.",
                    cancellationToken).ConfigureAwait(true);
            }

            var started = await cockpit.StartSessionOnWorkspaceAsync(
                workspace.Id, profile, request.Prompt, request.WorkingDirectory, request.SessionName, requestedKind,
                launchOptions, request.IsolateInWorktree, explicitProjectId: project?.Id).ConfigureAwait(true);

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
                Refusal: null,
                ProjectId: project?.Id), cancellationToken).ConfigureAwait(true);

            return AgentSpawnResult.Started(pane.PaneId, pane.Name, request.WorkingDirectory, pane.PromptDelivered, resolvedProfileLabel: profile.Label);
        }).ConfigureAwait(false);
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

    private Task<AgentStopResult> _StopAsync(string paneId, SpawnCaller caller, string? callerPaneId, CancellationToken cancellationToken) =>
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
                // Who actually asked, not who used to be the only one who could (AC-795).
                caller,
                callerPaneId,
                workspaceId ?? string.Empty,
                _FindWorkspace(workspaceId)?.Name,
                session.ActiveProfileLabel,
                WorkingDirectory: null,
                paneId,
                name,
                Refusal: null), cancellationToken).ConfigureAwait(true);

            return AgentStopResult.Stopped(paneId, name);
        });

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

            // The agent line's own answer to "is this a live agent session, and does it hear at turn start" — asked of
            // the addressee's pane rather than of a caller's desk, which is what makes this reach every desk without
            // changing what any other sender may reach. A pane that is not an agent session (a plain terminal), or
            // that no longer exists, resolves to nothing here.
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

    public Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default) =>
        _OnUiThreadAsync(async () =>
        {
            // The same three refusals as StopAsync, in the same order and for the same reasons — see the comments
            // there. A pane the assistant may not end is a pane it may not speak as either.
            if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
            {
                return await _RefusePromptAsync(paneId, "That is my own session, and I do not get to hand myself a turn.", cancellationToken).ConfigureAwait(true);
            }

            if (cockpit.Sessions.FirstOrDefault(candidate => string.Equals(candidate.PaneId, paneId, StringComparison.Ordinal)) is not { } session)
            {
                var elsewhere = cockpit.FindSession(paneId);
                return await _RefusePromptAsync(paneId, elsewhere is null
                        ? $"There is no session with pane id '{paneId}' — it may already have been closed."
                        : $"'{elsewhere.Title}' runs inside a workspace's own surface rather than as a pane, so I cannot hand it a turn. Whoever started it drives it.",
                    cancellationToken).ConfigureAwait(true);
            }

            if (!session.ShowPluginHeaderItems)
            {
                return await _RefusePromptAsync(paneId, $"'{session.Title}' is a terminal pane, not an agent session.", cancellationToken).ConfigureAwait(true);
            }

            // Asked before handing anything over, not after: a pane that is still coming up holds exactly one brief,
            // so a second arriving first would be refused by SubmitPromptWhenReady and reported here as "held" —
            // about a brief belonging to the earlier call. The model is told plainly instead, which is also what
            // stops delivered:false from reading as an invitation to try again.
            if (session.HasPromptWaitingToBeDelivered)
            {
                return await _RefusePromptAsync(
                    paneId,
                    $"'{session.Title}' is still starting and already has a turn waiting. It gets the one it was given first; this one was not accepted.",
                    cancellationToken).ConfigureAwait(true);
            }

            // Held rather than dropped when the session is still coming up, and the caller is told which of the two
            // happened — see SessionPanelViewModel.SubmitPromptWhenReady.
            var delivered = session.SubmitPromptWhenReady(prompt);

            await _RecordAsync(new AssistantSpawnAuditEntry(
                DateTimeOffset.Now,
                AssistantSpawnAction.Prompt,
                SpawnCaller.Assistant,
                CallerPaneId: null,
                session.WorkspaceId ?? string.Empty,
                _FindWorkspace(session.WorkspaceId)?.Name,
                session.ActiveProfileLabel,
                WorkingDirectory: null,
                paneId,
                session.Title,
                Refusal: null), cancellationToken).ConfigureAwait(true);

            return AgentPromptResult.Handed(paneId, session.Title, delivered);
        });

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
            reason), cancellationToken).ConfigureAwait(true);

        return AgentPromptResult.Refused(reason);
    }

    public Task<AssistantRenameResult> RenameSessionAsync(string paneId, string name, CancellationToken cancellationToken = default) =>
        _OnUiThreadAsync(() => Task.FromResult(_RenameSession(paneId, name)));

    // Not on the spawn trail, and deliberately: that record exists because a spawn starts a process and spends
    // money. A rename costs nothing, is reversible, and shows up on the operator's own screen as it happens.
    private AssistantRenameResult _RenameSession(string paneId, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return AssistantRenameResult.Refused("A session needs a name; that one was empty.");
        }

        // By identity rather than by whether it happens to be findable — the same rule, and the same reason, as
        // _StopAsync: the assistant sits outside Sessions today, but that is where it sits and not a rule.
        if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
        {
            return AssistantRenameResult.Refused("That is my own session, and I do not get to name it.");
        }

        if (cockpit.SetSessionName(paneId, trimmed))
        {
            return AssistantRenameResult.Renamed(trimmed);
        }

        // SetSessionName reaches Sessions only, so false past the guards above means the pane is not one the cockpit
        // holds. FindSession separates the two cases the assistant can actually be looking at, because it lists
        // embedded panes and would otherwise be told they had closed.
        var elsewhere = cockpit.FindSession(paneId);
        return AssistantRenameResult.Refused(elsewhere is null
            ? $"There is no session with pane id '{paneId}' — it may already have been closed."
            : $"'{elsewhere.Title}' runs inside a workspace's own surface rather than as a pane, so I cannot rename it.");
    }

    public Task<AssistantRenameResult> RenameWorkspaceAsync(string workspaceId, string name, CancellationToken cancellationToken = default) =>
        _OnUiThreadAsync(async () =>
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0)
            {
                return AssistantRenameResult.Refused("A workspace needs a name; that one was empty.");
            }

            // Looked up here rather than left to the rename itself, which returns silently for a desk it cannot
            // find — and a silent no-op would come back to the assistant as a rename that happened.
            if (_FindWorkspace(workspaceId) is not { } workspace)
            {
                return AssistantRenameResult.Refused(
                    $"There is no workspace with id '{workspaceId}'. List the workspaces and name one of those.");
            }

            await cockpit.Workspaces.RenameWorkspaceAsync((workspace.Id, trimmed)).ConfigureAwait(true);
            return AssistantRenameResult.Renamed(trimmed);
        });

    public Task<IReadOnlyList<AssistantWorkspaceRow>> ListWorkspacesAsync(CancellationToken cancellationToken = default) =>
        _OnUiThreadAsync(() => Task.FromResult(_ListWorkspaces()));

    // The profiles, straight off the store. No UI thread: this reads a file, not the cockpit's collections.
    //
    // *What each profile is configured to run at comes with it* (AC-647). The alternative — the assistant reading
    // `cockpit.json` — is not a route it has at all: it holds MCP tools and nothing else. What is reported is the
    // provider's own declared schema (AC-649) filled in from the profile, so Claude's permission mode/model/effort
    // and Codex's sandbox each arrive in their own vocabulary rather than as a settings dump with three Claude-shaped
    // slots to force them into.
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
                    ProfileDisplay.ModelOf(profile))
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

        return await _OnUiThreadAsync(async () =>
        {
            var created = await cockpit.Workspaces.CreateSessionsWorkspaceAsync(trimmed).ConfigureAwait(true);
            return (AssistantWorkspaceRow?)new AssistantWorkspaceRow(
                created.Id, created.Name, created.Type.Id, CanHostSessions: true, SessionCount: 0, IsActive: true);
        }).ConfigureAwait(false);
    }

    // Closes an empty sessions desk. Narrower than the tab's ✕, deliberately: it refuses that button's three
    // reasons, refuses every desk that is not a sessions desk, and does the confirmation dialog's job by refusing
    // rather than by asking.
    // *Why the emptiness check is here and not left to `CockpitViewModel.CloseWorkspaceAsync`.*
    // That method closes the desk *and everything on it*, which is right behind a dialog that first names
    // what is about to be stopped. There is no dialog on this route: what the operator approves is an Allow row
    // naming a desk, and taking three running sessions with it is work nobody asked for and nothing showed them.
    // So the sessions go first, through `stop_agent` and its own approval each, and this refuses until there
    // are none — at which point, on a sessions desk, the two paths do the same thing to the same desk.
    //
    // *Only a sessions desk, and this is where the two paths part.* A dashboard's occupants are widgets and a
    // plugin desk's are whatever that plugin holds; neither is counted below, so both read as empty and were
    // closed on the spot, taking an arrangement nobody was shown and nothing can rebuild. The ✕ path names what
    // goes ("It holds N widgets… this cannot be undone") because it has a dialog to name it in; a consent card
    // cannot enumerate what is about to be lost, so the honest answer here is not a better warning but a smaller
    // tool. Whole categories are refused rather than emptiness being redefined per type: what "empty" means on a
    // desk type this tool has never seen is not something it can be written to know.
    public Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) =>
        _OnUiThreadAsync(async () =>
        {
            if (_FindWorkspace(workspaceId) is not { } workspace)
            {
                return WorkspaceRemovalResult.Refused(
                    $"There is no workspace with id '{workspaceId}'. List the workspaces and name one of those.");
            }

            // The button's own gate, asked rather than re-derived — CanClose is what greys out the ✕, and the two
            // reasons it says no for are worth telling apart out loud.
            if (!cockpit.Workspaces.CanClose(workspaceId))
            {
                return WorkspaceRemovalResult.Refused(workspace.Type == WorkspaceType.Projects
                    ? $"'{workspace.Name}' is the projects overview. It is always there, and closing it is not something anyone can do."
                    : $"'{workspace.Name}' is the only desk left, and the cockpit always needs one to show.");
            }

            // Before the session count, because that count is about sessions and a desk of another type has none —
            // it would read as empty and the close would go through.
            if (workspace.Type != WorkspaceType.Sessions)
            {
                return WorkspaceRemovalResult.Refused(
                    $"'{workspace.Name}' is not a sessions desk — it is a {workspace.Type.Id} desk, and this tool only closes the ones that hold sessions. What is on it is not sessions I can count or stop, so closing it is the operator's own to do from its tab. Nothing is lost by asking them.");
            }

            var occupants = _CountEverythingOn(workspaceId);
            if (occupants > 0)
            {
                return WorkspaceRemovalResult.Refused(occupants == 1
                    ? $"There is still 1 session on '{workspace.Name}'. Stop it first — I do not close a desk with work still on it."
                    : $"There are still {occupants} sessions on '{workspace.Name}'. Stop them first — I do not close a desk with work still on it.");
            }

            await cockpit.CloseWorkspaceAsync(workspaceId).ConfigureAwait(true);
            return WorkspaceRemovalResult.Removed(workspace.Name);
        });

    // How many sessions closing this desk would take with it, by the same placement rule the roster reports —
    // so the number the operator just heard from `list_workspaces` is the number this refuses on.
    // Wider than that roster in one way, deliberately: it does not filter on `ShowPluginHeaderItems`. A plain
    // terminal is not an agent session and so is not counted there, but the close would end it just the same, and a
    // pty killed by a call about a desk is the loss this refusal exists to prevent. The assistant's own pane is
    // excluded by `SessionWorkspacePlacement` itself, which resolves it to no desk at all.
    private int _CountEverythingOn(string workspaceId)
    {
        var firstSessionsWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(cockpit.Workspaces.Settings);
        return cockpit.AllSessions().Count(session => string.Equals(
            SessionWorkspacePlacement.Resolve(session, firstSessionsWorkspaceId), workspaceId, StringComparison.Ordinal));
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
            reason,
            ProjectId: request.ProjectId), cancellationToken).ConfigureAwait(true);

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

    // AC-640: the watcher decides everything — whether the pane resolves, whether it keeps a transcript, whether the
    // pattern compiles — because it is the half that has to live with the answer every tick. What happens here is
    // only getting onto the thread its probe reads the session list on.
    public Task<AssistantWatchResult> WatchSessionAsync(
        string paneId,
        IReadOnlyList<string>? events,
        int? afterMinutes = null,
        string? pattern = null,
        CancellationToken cancellationToken = default) =>
        _OnUiThreadAsync(() => Task.FromResult(watcher.Watch(paneId, events, afterMinutes, pattern)));

    public Task<bool> UnwatchSessionAsync(string paneId, CancellationToken cancellationToken = default) =>
        _OnUiThreadAsync(() => Task.FromResult(watcher.Unwatch(paneId)));

    // AC-719 ronde B: re-owns a worktree the assistant made for itself onto a running session, via the same
    // ReattachAsync primitive the reattach guard in _ResolveIsolatedWorkingDirectoryAsync uses. Every refusal here
    // is hard, not best-effort — a wrong target could pull a worktree out from under a session that needed it.
    public Task<WorktreeHandoverResult> HandoverWorktreeAsync(string path, string paneId, CancellationToken cancellationToken = default) =>
        _OnUiThreadAsync(async () =>
        {
            if (worktreeManager is null)
            {
                return await _RefuseHandoverAsync(path, paneId, "Worktree management is not available here.", cancellationToken).ConfigureAwait(true);
            }

            if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
            {
                return await _RefuseHandoverAsync(path, paneId, "That is my own session; a worktree cannot be handed to it.", cancellationToken).ConfigureAwait(true);
            }

            var full = Path.GetFullPath(path);
            var record = (await worktreeManager.ListAsync(cancellationToken).ConfigureAwait(true))
                .FirstOrDefault(candidate => string.Equals(Path.GetFullPath(candidate.Path), full, _PathComparison));
            if (record is null)
            {
                return await _RefuseHandoverAsync(path, paneId, "No managed worktree at that path — call worktree_list for the current paths.", cancellationToken).ConfigureAwait(true);
            }

            if (!string.Equals(record.SessionId, AssistantIdentity.PaneId, StringComparison.Ordinal))
            {
                return await _RefuseHandoverAsync(record.Path, paneId, "That worktree is not mine to hand over — it belongs to a different session.", cancellationToken).ConfigureAwait(true);
            }

            if (cockpit.Sessions.FirstOrDefault(candidate => string.Equals(candidate.PaneId, paneId, StringComparison.Ordinal)) is not { } session)
            {
                var elsewhere = cockpit.FindSession(paneId);
                return await _RefuseHandoverAsync(record.Path, paneId, elsewhere is null
                        ? $"There is no session with pane id '{paneId}' — it may already have been closed."
                        : $"'{elsewhere.Title}' runs inside a workspace's own surface rather than as a pane, so a worktree cannot be handed to it.",
                    cancellationToken).ConfigureAwait(true);
            }

            if (!session.ShowPluginHeaderItems)
            {
                return await _RefuseHandoverAsync(record.Path, paneId, $"'{session.Title}' is a terminal pane, not an agent session.", cancellationToken)
                    .ConfigureAwait(true);
            }

            if (await worktreeManager.ReattachAsync(record.Path, paneId, cancellationToken).ConfigureAwait(true) is not { } reattached)
            {
                return await _RefuseHandoverAsync(record.Path, paneId, "The worktree could not be re-owned — it may have just been removed.", cancellationToken)
                    .ConfigureAwait(true);
            }

            session.WorktreeBranch = reattached.Branch;

            await _RecordAsync(new AssistantSpawnAuditEntry(
                DateTimeOffset.Now,
                AssistantSpawnAction.Handover,
                SpawnCaller.Assistant,
                CallerPaneId: null,
                session.WorkspaceId ?? string.Empty,
                _FindWorkspace(session.WorkspaceId)?.Name,
                Profile: null,
                WorkingDirectory: reattached.Path,
                paneId,
                session.Title,
                Refusal: null), cancellationToken).ConfigureAwait(true);

            return WorktreeHandoverResult.HandedOver(reattached.Path, reattached.Branch, session.Title);
        });

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
            reason), cancellationToken).ConfigureAwait(true);

        return WorktreeHandoverResult.Refused(reason);
    }

    // AC-798: the "Add to my projects…" dialog's own route, minus the window. Every step is the dialog's —
    // `PrepareBindingAsync`'s one-time read, `SharedProjectBindingDialogViewModel`'s composition, `ToProject`, and
    // `ProjectsViewModel`'s own persisting — so what lands in `cockpit.json` is what the operator would have got by
    // clicking, rather than a second assembly of the same fields that can drift from it.
    //
    // *What this refuses, it refuses with the question in it.* The folder, the profile and a machine-specific
    // resource row's reference are the three things the shared definition deliberately does not carry, and a value
    // invented here would be a fact about this machine that nobody chose. So each missing one comes back as a
    // sentence the assistant can put to the operator — not a default, and not a blank field quietly dropped on save.
    //
    // *It does not clone* (criterion 7): the folder has to exist. A clone writes a checkout to a path the assistant
    // picked, which is a different kind of act from registering a project, and it is not on this door.
    public async Task<AssistantProjectBindResult> BindSharedProjectAsync(
        string sharedProjectId,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences = null,
        CancellationToken cancellationToken = default)
    {
        if (sharedProjectSources is null)
        {
            return AssistantProjectBindResult.Refused(
                "No connection on this machine offers shared projects, so there is nothing to add from.");
        }

        // The registry and the visibility filter in one UI-thread hop. `SharedProjectSourceRegistry` keeps a plain
        // dictionary that a plugin's own settings screen adds to on that thread, so enumerating `Sources` from this
        // Kestrel request thread is a torn read waiting to happen — the same reason `AssistantReadGateway` reads it
        // there rather than where the call arrives.
        var (sources, boundIds, hiddenIds) = await _OnUiThreadAsync(() =>
        {
            var (bound, hidden) = cockpit.Projects.SharedProjectVisibilityFilterIds();
            return Task.FromResult((sharedProjectSources.Sources, bound, hidden));
        }).ConfigureAwait(false);

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

        var (viewModel, error) = await SharedProjectBindingDialogViewModel
            .CreateAsync(id, source.SourceName, source, profiles, cancellationToken).ConfigureAwait(false);
        if (viewModel is null)
        {
            // The definition read failed — unreachable, signed out, or the project gone between list_shared_projects
            // and this call. The source's own contract says that arrives as `SharedProjectBindingResult.Failed`
            // rather than as an exception, and it is passed on rather than summarised: the assistant is about to
            // read it out, and "could not add it" tells the operator nothing they can act on.
            return AssistantProjectBindResult.Refused(error ?? "Could not read this project's definition.");
        }

        // The "Choose…" route, not the "Clone…" one — so, exactly as `ApplyPickedDirectory` does for the operator's
        // own pick, the shared definition's `GitUrl` is dropped: the folder was pointed at rather than cloned from
        // it, and keeping the URL would claim a provenance nothing here established.
        viewModel.ApplyPickedDirectory(directory);
        viewModel.SelectedProfileLabel = profile.Label;

        if (_FillResourceRows(viewModel, resourceReferences) is { } rowRefusal)
        {
            return AssistantProjectBindResult.Refused(rowRefusal);
        }

        var stored = await _OnUiThreadAsync(
            () => cockpit.Projects.AddBoundProjectAsync(viewModel.ToProject())).ConfigureAwait(false);

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
        // AC-799 review finding 8: production DI always registers a real `IMcpServerCatalog` (`ISingletonService`,
        // scanned), so this is unreachable there. Kept as a refusal — the same shape `sharedProjectSources` being
        // null takes below — rather than a no-op catalog that nothing past this line would ever actually read.
        if (mcpServerCatalog is null)
        {
            return AssistantProjectCreateResult.Refused("MCP servers are not available here, so a project cannot be created.");
        }

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

        // AC-799 review finding 3: validated by label, case-insensitively, against what `list_profiles` actually
        // reports — the same rule `SpawnAsync` and `BindSharedProjectAsync` already hold `profile` to. Left
        // unchecked, this would have been a second, looser validation surface than either: the dialog's own
        // `SelectedProfileLabel` carries no such guard itself — it exists so a human can only ever pick from a
        // bound combo box, a guarantee this call does not get for free by setting the property directly.
        if (!string.IsNullOrWhiteSpace(defaultProfileLabel))
        {
            var known = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!known.Any(candidate => string.Equals(candidate.Label, defaultProfileLabel.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var labels = known.Count == 0 ? "none are configured" : string.Join(", ", known.Select(candidate => $"'{candidate.Label}'"));
                return AssistantProjectCreateResult.Refused(
                    $"There is no profile called '{defaultProfileLabel}'. The profiles this cockpit knows are: {labels}.");
            }
        }

        var project = await _OnUiThreadAsync(async () =>
        {
            var viewModel = await ProjectDialogViewModel.CreateAsync(
                null, profiles, mcpServerCatalog, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            viewModel.Name = name;
            viewModel.Description = description ?? string.Empty;
            viewModel.SourceDirectory = sourceDirectory ?? string.Empty;
            viewModel.BehaviorPrompt = behaviorPrompt ?? string.Empty;
            viewModel.IsolateInWorktreeByDefault = isolateInWorktreeByDefault;
            viewModel.Category = category ?? string.Empty;
            viewModel.SelectedProfileLabel = string.IsNullOrWhiteSpace(defaultProfileLabel) ? null : defaultProfileLabel.Trim();

            return viewModel.CanSave ? viewModel.ToProject() : null;
        }).ConfigureAwait(false);

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

        var stored = await _OnUiThreadAsync(() => cockpit.Projects.AddNewProjectAsync(withDynamicFields)).ConfigureAwait(false);
        return AssistantProjectCreateResult.Created(stored.Id, stored.Name);
    }

    // The same registry and per-source timeout `list_shared_projects` itself reads through
    // (`ProjectsViewModel._ListWithTimeoutAsync`), not a second copy of either — a source that is slow, signed
    // out or unreachable is skipped rather than failing the whole check. Null when nothing matches.
    private async Task<(string SourceName, string Id, string Name)?> _FindSharedProjectByNameAsync(
        string name, CancellationToken cancellationToken)
    {
        if (sharedProjectSources is null)
        {
            return null;
        }

        var (sources, boundIds, hiddenIds) = await _OnUiThreadAsync(() =>
        {
            var (bound, hidden) = cockpit.Projects.SharedProjectVisibilityFilterIds();
            return Task.FromResult((sharedProjectSources.Sources, bound, hidden));
        }).ConfigureAwait(false);

        if (sources.Count == 0)
        {
            return null;
        }

        var results = await Task.WhenAll(sources.Select(source => ProjectsViewModel._ListWithTimeoutAsync(source, cancellationToken)))
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

    // Fills the machine-specific resource rows (AC-246) the shared definition names but carries no value for: the
    // role and the label travel, the reference never does. One reference each, in the order the definition lists
    // them, or a refusal naming every row — positional rather than keyed because two rows may carry the same label
    // and a key that collides would fill one row twice and leave the other blank.
    //
    // A blank row is not an error the dialog reports either; it is simply dropped on save. That is fine behind a
    // window where the operator sees the empty box they left — here nobody would see it, and the project would come
    // out quietly missing a resource. Hence: refused, with the rows spelled out. Returns null when there was
    // nothing to ask about or everything was answered.
    private static string? _FillResourceRows(SharedProjectBindingDialogViewModel viewModel, IReadOnlyList<string>? references)
    {
        var rows = viewModel.ResourceRows;
        var given = references?.Where(reference => !string.IsNullOrWhiteSpace(reference)).ToList() ?? [];

        if (given.Count != rows.Count)
        {
            return rows.Count == 0
                ? "This project names no resources whose location is this machine's own, so there is nothing to pass in resources."
                : $"This project names {rows.Count} resource(s) whose location is this machine's own, and the shared definition does not carry them. "
                    + "Ask the operator for a local path for each, then call again with resources holding one entry per row, in this order: "
                    + string.Join("; ", rows.Select((row, index) => $"{index + 1}. {row.DisplayLabel}"))
                    + ".";
        }

        for (var index = 0; index < rows.Count; index++)
        {
            rows[index].Reference = given[index].Trim();
        }

        return null;
    }

    private Task _RecordAsync(AssistantSpawnAuditEntry entry, CancellationToken cancellationToken) =>
        auditLog.RecordAsync(entry, cancellationToken);

    // Runs `work` on the UI thread — inline when already there, so a test on the UI thread pays for
    // no redundant dispatch. Same rule as `AssistantReadGateway`, awaited rather than blocked on.
    private static Task<T> _OnUiThreadAsync<T>(Func<Task<T>> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);
}
