using System.Text.Json;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Provisioning;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1386 (F5.4): sessions and the assistant as JSON commands, on the same gateways and the same policy as the node
// tools, so there is no second route to a session. Seeing is reaching: a pane outside the key's scope is a 404.
internal static class SessionsEndpoints
{
    private const string NotStarted = "not_started";

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        // Resolved per request: the API is mapped before a host that serves no sessions has registered their seams.
        IAssistantReadGateway read() => services.GetRequiredService<IAssistantReadGateway>();
        IAssistantAgentGateway gateway() => services.GetRequiredService<IAssistantAgentGateway>();
        ISessionRegistry sessions() => services.GetRequiredService<ISessionRegistry>();
        IBackendEventLog log() => services.GetRequiredService<IBackendEventLog>();
        NodeCallerSessionPolicy policy() => new(read(), gateway(), services.GetRequiredService<INodePairingBroker>(), services.GetRequiredService<ISessionProfileStore>(), services.GetService<IProfileLoginChecker>());

        // AC-1388: `seq` is read before the list, so a reader following the stream after it misses no change to it.
        api.MapGet("/sessions", async () =>
        {
            var caller = _Caller();
            var seq = log().LastSeq;
            var visible = await policy().VisibleSessionsAsync(caller).ConfigureAwait(false);
            var pending = (await read().ListPendingPermissionsAsync().ConfigureAwait(false)).ToLookup(permission => permission.PaneId, StringComparer.Ordinal);
            var profiles = (await services.GetRequiredService<ISessionProfileStore>().LoadAsync().ConfigureAwait(false))
                .ToDictionary(profile => profile.Label, StringComparer.Ordinal);
            var providers = services.GetService<IPluginProviderRegistry>();
            List<object> listed = [];
            foreach (var session in visible)
            {
                profiles.TryGetValue(session.Profile, out var profile);
                var providerId = profile is null ? null : ProviderId(profile);
                var registration = providerId is null ? null : providers?.Resolve(providerId);
                RemoteUsageStatus? usageStatus = null;
                IReadOnlyList<RemoteQueueItem> queue = [];
                var handle = sessions().Find(session.PaneId);
                if (handle is not null)
                {
                    await handle.UseControlAsync(control =>
                    {
                        usageStatus = profile is null ? null : RemoteUsageStatus.From(control.ReadUsageStatus(profile.ProviderConfig));
                        queue = [.. control.Queue.Select(prompt => new RemoteQueueItem(prompt.WireId, prompt.Text))];
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }

                listed.Add(new
                {
                    paneId = session.PaneId,
                    name = session.Name,
                    profile = session.Profile,
                    providerId,
                    usageSignals = registration?.UsageSignals.Select(_UsageSignal).ToArray() ?? [],
                    usageStatus,
                    canSignIn = caller.Capability == ConnectKeyCapability.Admin && registration?.StartLogin is not null,
                    projectId = session.ProjectId,
                    statusline = session.Statusline,
                    status = session.Status,
                    needsYou = session.NeedsYou,
                    hasOutstandingWork = session.HasOutstandingWork,
                    worktreeBranch = handle?.WorktreeBranch,
                    kind = handle is Sessions.Tty.BackendTtySession ? "tty" : "sdk",
                    workspaceId = handle?.WorkspaceId,
                    placedWorkspaceId = handle?.PlacedWorkspaceId,
                    liveState = handle is null ? null : handle.LiveState with { Connection = null },
                    startedByTheAssistant = handle?.StartedByTheAssistant ?? false,
                    canTakeAPrompt = handle?.CanTakeAPrompt ?? false,
                    hasPendingConsent = handle?.HasPendingConsent ?? false,
                    queue,
                    pendingPermissions = pending[session.PaneId].Select(permission => new
                    {
                        toolUseId = permission.ToolUseId,
                        tool = permission.ToolName,
                        input = permission.InputJson,
                        sinceUtc = permission.SinceUtc,
                    }),
                });
            }

            return Results.Json(new
            {
                seq,
                workspaces = caller.Capability == ConnectKeyCapability.Admin && services.GetService<ISessionDesks>() is { } desks
                    ? await desks.RunExclusiveAsync(() => WorkspacesEndpoints.View(desks.Workspaces, visible.Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal))).ConfigureAwait(false)
                    : null,
                assistant = sessions().Assistant is { } assistant ? new { paneId = assistant.PaneId, name = assistant.Title } : null,
                sessions = listed,
            });
        }).RequireOperate();

        api.MapGet("/sessions/{paneId}/transcript", async (string paneId, int? count) =>
        {
            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false)
                || await read().ReadTranscriptAsync(paneId, _Count(count)).ConfigureAwait(false) is not { } transcript)
            {
                return Results.NotFound();
            }

            var snapshot = sessions().Find(paneId) is { } handle ? await handle.ReadRowsAtAsync(() => log().LastSeq).ConfigureAwait(false) : null;
            return Results.Json(new
            {
                paneId = transcript.PaneId,
                name = transcript.Name,
                totalEntries = transcript.TotalEntries,
                entries = transcript.Entries.Select(entry => new { kind = entry.Kind, text = entry.Text, toolResult = entry.ToolResult }),
                rows = snapshot?.Rows,
                seq = snapshot?.Seq,
            });
        }).RequireOperate();

        api.MapGet("/sessions/{paneId}/mcp", async (string paneId, CancellationToken cancellationToken) =>
        {
            var result = await gateway().GetSessionMcpSetAsync(paneId, cancellationToken).ConfigureAwait(false);
            return result.Ok
                ? Results.Json(new
                {
                    ok = true,
                    manager = result.Manager,
                    availableServers = result.AvailableServers.Select(server => new { name = server.Name, status = server.Status }),
                    servers = result.Servers.Select(server => new { name = server.Name, status = server.Status, reason = server.Reason, canRemove = server.CanRemove }),
                })
                : Results.NotFound();
        }).RequireAdmin().Audited("get_session_mcp_set", services);

        api.MapPut("/sessions/{paneId}/mcp", async (string paneId, McpSetChangeBody body, CancellationToken cancellationToken) =>
        {
            var result = await gateway().ChangeSessionMcpServerAsync(paneId, body.Name, body.Add, byOperator: true, cancellationToken).ConfigureAwait(false);
            return result.Ok
                ? Results.Json(new
                {
                    ok = true,
                    manager = result.Manager,
                    availableServers = result.AvailableServers.Select(server => new { name = server.Name, status = server.Status }),
                    servers = result.Servers.Select(server => new { name = server.Name, status = server.Status, reason = server.Reason, canRemove = server.CanRemove }),
                })
                : BackendApiRoutes.Error(StatusCodes.Status409Conflict, "mcp_change_refused", result.Error ?? "The MCP set could not be changed.");
        }).RequireAdmin().Audited("change_session_mcp_set", services);

        const string signInPath = "/sessions/{paneId}/mcp/{name}/sign-in";
        api.MapPost(signInPath, async (string paneId, string name, McpSignInStartBody body, CancellationToken cancellationToken) =>
        {
            if (body.Port is < 1024 or > 65535)
            {
                return BackendApiRoutes.Error(400, "invalid_request", "A loopback port from 1024 to 65535 is required.");
            }
            var set = await gateway().GetSessionMcpSetAsync(paneId, cancellationToken).ConfigureAwait(false);
            if (!set.Ok || !set.Servers.Concat(set.AvailableServers).Any(server => string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.NotFound();
            }
            var context = services.GetRequiredService<SessionMcpMounts>().Set(paneId)?.Context;
            var catalog = await services.GetRequiredService<IMcpServerCatalog>().GetServersForSessionAsync(
                context?.Project?.Id, new ExactMcpServerSelection([name]), cancellationToken).ConfigureAwait(false);
            var server = catalog.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (server is not { Auth: McpServerAuth.OAuth })
            {
                return Results.NotFound();
            }
            var signIn = await services.GetRequiredService<McpServerSignIns>().StartAsync(
                $"session:{paneId}", name, body.Port, async ct =>
                {
                    var access = await services.GetRequiredService<IMcpOAuthCoordinator>().AcquireAsync(server, interactive: true, ct).ConfigureAwait(false);
                    return access.State == McpAuthState.Authorized
                        ? Cockpit.Plugins.Abstractions.Mcp.PluginMcpSignInOutcome.Authorized
                        : Cockpit.Plugins.Abstractions.Mcp.PluginMcpSignInOutcome.Declined;
                }, _ => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
            return Results.Json(PluginEndpoints.McpSignInView(signIn), ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("start_session_mcp_sign_in", services);

        api.MapPost(signInPath + "/{flowId}/input", (string paneId, string name, string flowId, SignInInputBody body) =>
            services.GetRequiredService<McpServerSignIns>().Find($"session:{paneId}", name, flowId) is { } signIn && signIn.Submit(body.Text)
                ? Results.Json(PluginEndpoints.McpSignInView(signIn), ConnectKeyEndpoints.Json)
                : Results.NotFound()).RequireAdmin().Audited("send_session_mcp_sign_in_redirect", services);
        api.MapGet(signInPath + "/{flowId}", (string paneId, string name, string flowId) =>
            services.GetRequiredService<McpServerSignIns>().Find($"session:{paneId}", name, flowId) is { } signIn
                ? Results.Json(PluginEndpoints.McpSignInView(signIn), ConnectKeyEndpoints.Json)
                : Results.NotFound()).RequireAdmin();
        api.MapDelete(signInPath + "/{flowId}", (string paneId, string name, string flowId) =>
        {
            services.GetRequiredService<McpServerSignIns>().Find($"session:{paneId}", name, flowId)?.End("cancelled");
            return Results.Json(new { });
        }).RequireAdmin().Audited("end_session_mcp_sign_in", services);

        api.MapGet("/conversations", async (CancellationToken cancellationToken) =>
            Results.Json((await _VisibleConversationsAsync(services, _Caller(), cancellationToken).ConfigureAwait(false))
                .Select(record => record with { WorkingDirectory = null, TranscriptPath = null }))).RequireOperate();

        api.MapGet("/conversations/search", async (string? text, string? projectId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken) =>
        {
            var visible = (await _VisibleConversationsAsync(services, _Caller(), cancellationToken).ConfigureAwait(false))
                .Select(record => (record.PaneId, record.ProfileId, record.ProjectId, record.ConversationId)).ToHashSet();
            if (services.GetService<SessionTranscriptLog>() is { } transcripts)
            {
                await transcripts.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var found = await services.GetRequiredService<IConversationIndex>().SearchAsync(text ?? "", projectId, from, to, cancellationToken).ConfigureAwait(false);
            return Results.Json(found.Where(result => visible.Contains((result.Record.PaneId, result.Record.ProfileId, result.Record.ProjectId, result.Record.ConversationId))).Select(result => result with
            {
                Record = result.Record with { WorkingDirectory = null, TranscriptPath = null },
            }));
        }).RequireOperate();

        api.MapPost("/sessions", async (StartSessionBody body, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(body.Profile))
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "profile is required.");
            }

            var check = await policy().CheckStartAsync(_Caller(), body.Profile, body.ProjectId).ConfigureAwait(false);
            if (check.Profile is not { } allowedProfile)
            {
                return BackendApiRoutes.Error(StatusCodes.Status403Forbidden, "out_of_scope", check.Refusal ?? string.Empty);
            }

            ConversationRecord? conversation = null;
            if (body.Resume is { Mode: not SessionResumeMode.New } || body.PaneId is not null)
            {
                if (body.Kind == "tty")
                {
                    return BackendApiRoutes.Error(409, NotStarted, "Recorded SDK conversations require the SDK route.");
                }
                if (body.Resume is not { Mode: SessionResumeMode.BySessionId or SessionResumeMode.MostRecent } resume)
                {
                    return Results.NotFound();
                }
                var visible = await _VisibleConversationsAsync(services, _Caller(), cancellationToken).ConfigureAwait(false);
                conversation = visible.FirstOrDefault(record =>
                    string.Equals(record.ProfileId, allowedProfile.Label, StringComparison.OrdinalIgnoreCase)
                    && record.ProjectId == body.ProjectId
                    && (body.PaneId is null || record.PaneId == body.PaneId)
                    && (resume.Mode == SessionResumeMode.MostRecent || record.ConversationId == resume.SessionId));
                if (conversation is null)
                {
                    return Results.NotFound();
                }
                if (conversation.WorkingDirectory is { Length: > 0 } directory && !Directory.Exists(directory))
                {
                    return BackendApiRoutes.Error(409, NotStarted, "The directory this conversation ran in no longer exists on the server.");
                }
                if (sessions().Find(conversation.PaneId) is { } existing)
                {
                    if (!await policy().IsVisibleAsync(_Caller(), existing.PaneId).ConfigureAwait(false)
                        || !string.Equals(existing.ActiveProfileLabel, allowedProfile.Label, StringComparison.OrdinalIgnoreCase)
                        || existing.ProjectId != body.ProjectId)
                    {
                        return Results.NotFound();
                    }
                    if (existing is not IHostedSession { AwaitsStart: true })
                    {
                        return BackendApiRoutes.Error(409, NotStarted, "This conversation is already running.");
                    }
                }
            }

            var workspaceId = body.WorkspaceId;
            if (!string.IsNullOrEmpty(workspaceId))
            {
                if (_Caller().Capability != ConnectKeyCapability.Admin)
                {
                    return BackendApiRoutes.Error(403, "out_of_scope", "Choosing a workspace requires an admin key.");
                }
                var desks = services.GetRequiredService<ISessionDesks>();
                if (!await desks.RunExclusiveAsync(() => desks.Workspaces.Workspaces.Any(workspace =>
                    workspace.Id == workspaceId && workspace.Type == Cockpit.Core.Workspaces.WorkspaceType.Sessions)).ConfigureAwait(false))
                {
                    return BackendApiRoutes.Error(409, NotStarted, "The selected workspace cannot hold a session.");
                }
            }
            else
            {
                workspaceId = await policy().ActiveWorkspaceIdAsync().ConfigureAwait(false);
            }
            if (workspaceId is null)
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, NotStarted, "This node has no desk that can hold a session just now.");
            }

            if (conversation is not null)
            {
                if (services.GetService<SessionTranscriptLog>() is { } transcripts)
                {
                    await transcripts.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                var resumed = await services.GetRequiredService<ISessionLauncher>().StartSessionAsync(new SessionLaunchRequest(
                    workspaceId, allowedProfile, body.Prompt, conversation.WorkingDirectory, body.Name ?? conversation.Title,
                    Cockpit.Core.Workspaces.PaneSessionKind.Sdk, null, false, conversation.ProjectId, false,
                    SessionResume.BySessionId(conversation.ConversationId ?? throw new InvalidOperationException("A resumable conversation has no id.")),
                    PaneId: conversation.PaneId) { Role = conversation.Role }).ConfigureAwait(false);
                return resumed is { FailureReason: null, WorktreeDecision: null }
                    ? Results.Json(new { resumed.PaneId, resumed.Name, resumed.PromptDelivered }, statusCode: 201)
                    : BackendApiRoutes.Error(409, NotStarted, resumed?.FailureReason ?? "The conversation could not be resumed.");
            }

            // The node tool's spawn: the desk read here, the profile as this machine spells it, and no folder or
            // provider options from the caller.
            var result = await gateway().SpawnAsync(new AgentSpawnRequest(
                SpawnTarget.RequestedByThePairedController(workspaceId),
                allowedProfile.Label,
                body.ProjectId,
                body.Prompt,
                WorkingDirectory: null,
                SessionName: body.Name, Kind: body.Kind, Role: body.Role)).ConfigureAwait(false);

            return result.Ok
                ? Results.Json(new
                {
                    paneId = result.PaneId,
                    name = result.SessionName,
                    resolvedProfile = result.ResolvedProfileLabel,
                    promptDelivered = result.PromptDelivered,
                    mcpServers = result.McpServers,
                    mcpServersConfirmed = result.McpServersConfirmed,
                    role = result.Role,
                    roleList = result.RoleList,
                }, statusCode: StatusCodes.Status201Created)
                : BackendApiRoutes.Error(StatusCodes.Status409Conflict, NotStarted, result.Error ?? string.Empty);
        }).RequireOperate().Audited("start_session", services);

        api.MapPost("/sessions/{paneId}/prompt", async (string paneId, PromptBody body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text))
            {
                return _TextRequired();
            }

            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            var result = await gateway().SendPromptAsync(paneId, body.Text).ConfigureAwait(false);
            return result.Ok
                ? Results.Json(new { paneId = result.PaneId, name = result.SessionName, delivered = result.Delivered })
                : BackendApiRoutes.Error(StatusCodes.Status409Conflict, "not_delivered", result.Error ?? string.Empty);
        }).RequireOperate().Audited("send_prompt", services);

        api.MapDelete("/sessions/{paneId}", async (string paneId) =>
        {
            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            var result = await gateway().StopAsync(paneId, SpawnCaller.Controller, NodeCallerIdentity.PaneId).ConfigureAwait(false);
            return result.Ok
                ? Results.Json(new { paneId = result.PaneId, name = result.SessionName })
                : BackendApiRoutes.Error(StatusCodes.Status409Conflict, "not_stopped", result.Error ?? string.Empty);
        }).RequireOperate().Audited("stop_session", services);

        api.MapPost("/sessions/{paneId}/rename", async (string paneId, SessionNameBody body) =>
        {
            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false))
            {
                return Results.NotFound();
            }
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return _TextRequired();
            }
            var renamed = await services.GetRequiredService<ISessionLauncher>().SetSessionNameAsync(paneId, body.Name).ConfigureAwait(false);
            if (renamed)
            {
                log().Append("sessions-changed", null, new { });
            }
            return Results.Json(new { renamed });
        }).RequireOperate().Audited("rename_session", services);

        api.MapPost("/sessions/{paneId}/verify", async (string paneId, VerifyResultBody body) =>
        {
            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false) || sessions().Find(paneId) is not { } handle)
            {
                return Results.NotFound();
            }
            if (body.ScreenshotPng is not { Length: > 8 and <= 4 * 1024 * 1024 } png
                || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                return BackendApiRoutes.Error(400, "invalid_request", "A PNG screenshot of at most 4 MiB is required.");
            }
            return Results.Json(new { accepted = await handle.FeedVerifyResultAsync(body.Caption, png).ConfigureAwait(false) });
        }).RequireOperate().Audited("submit_verify_result", services);

        api.MapPost("/sessions/{paneId}/permissions/{toolUseId}", async (string paneId, string toolUseId, PermissionBody body) =>
        {
            if (!_Caller().MayAnswerPermissions)
            {
                return BackendApiRoutes.Error(StatusCodes.Status403Forbidden, "forbidden", NodeSessionMcpTools.PermissionsRefusal);
            }

            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            // AC-1476: after the grant check, so a key without it learns nothing about which scopes exist.
            var scope = body.Scope ?? "once";
            var forSession = scope == "session";
            if (!forSession && scope != "once" || forSession && !body.Allow)
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "scope must be once, or session on an allow.");
            }

            if (body.AnswersJson is { } answersJson)
            {
                if (forSession || !body.Allow || !_IsStructuredAnswer(answersJson))
                {
                    return BackendApiRoutes.Error(400, "invalid_request", "Structured answers require an allow for this question only.");
                }
                var answeredQuestion = sessions().Find(paneId) is { } handle
                    && await handle.RespondToPermissionWithAnswersAsync(toolUseId, body.Allow, answersJson).ConfigureAwait(false);
                return Results.Json(new { paneId, toolUseId, answered = answeredQuestion });
            }

            var answered = await (forSession
                ? gateway().RespondToPermissionForSessionAsync(paneId, toolUseId)
                : gateway().RespondToPermissionAsync(paneId, toolUseId, body.Allow)).ConfigureAwait(false);
            return Results.Json(new { paneId, toolUseId, answered });
        }).RequireOperate().Audited("answer_permission", services);

        api.MapPost("/sessions/{paneId}/interrupt", async (string paneId) =>
        {
            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false)
                || sessions().Find(paneId) is not { } handle
                || !await handle.UseControlAsync(control => control.InterruptAsync()).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            return Results.Json(new { paneId });
        }).RequireOperate().Audited("interrupt_session", services);

        api.MapPost("/sessions/{paneId}/model", async (string paneId, ModelBody body) =>
        {
            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false)
                || sessions().Find(paneId) is not { } handle
                || !await handle.UseControlAsync(control => control.SetModelAsync(body.Model)).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            return Results.Json(new { paneId, model = body.Model });
        }).RequireOperate().Audited("set_session_model", services);

        api.MapPost("/sessions/{paneId}/permission-mode", async (string paneId, PermissionModeBody body) =>
        {
            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false)
                || sessions().Find(paneId) is not { } handle)
            {
                return Results.NotFound();
            }

            if (body.Mode is not ("default" or "acceptEdits" or "plan"))
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "mode must be default, acceptEdits, or plan.");
            }

            if (body.Mode == "acceptEdits" && !_Caller().MayAnswerPermissions)
            {
                return BackendApiRoutes.Error(StatusCodes.Status403Forbidden, "forbidden", NodeSessionMcpTools.PermissionsRefusal);
            }

            try
            {
                if (!await handle.UseControlAsync(control => control.SetPermissionModeAsync(body.Mode)).ConfigureAwait(false))
                {
                    return Results.NotFound();
                }
            }
            catch (OrgPolicyRefusedException refused)
            {
                return BackendApiRoutes.Error(StatusCodes.Status403Forbidden, "forbidden", refused.Message);
            }

            return Results.Json(new { paneId, mode = body.Mode });
        }).RequireOperate().Audited("set_session_permission_mode", services);

        api.MapPost("/sessions/{paneId}/queue", async (string paneId, QueueCommandBody body) =>
        {
            if (!await policy().IsVisibleAsync(_Caller(), paneId).ConfigureAwait(false)
                || sessions().Find(paneId) is not { } handle)
            {
                return Results.NotFound();
            }

            IResult? answer = null;
            var used = await handle.UseControlAsync(async control =>
            {
                if (string.Equals(body.Command, "clear", StringComparison.Ordinal))
                {
                    control.ClearQueue();
                    answer = Results.Json(new { paneId });
                    return;
                }

                if (string.Equals(body.Command, "withdraw", StringComparison.Ordinal))
                {
                    var queued = control.Queue.FirstOrDefault(prompt => string.Equals(prompt.WireId, body.WireId, StringComparison.Ordinal));
                    answer = queued is null || !control.Withdraw(queued)
                        ? BackendApiRoutes.Error(StatusCodes.Status404NotFound, "queue_item_not_found", "The queue item was not found.")
                        : Results.Json(new { paneId, wireId = queued.WireId });
                    return;
                }

                if ((string.Equals(body.Command, "submit", StringComparison.Ordinal)
                        || string.Equals(body.Command, "enqueue", StringComparison.Ordinal))
                    && !string.IsNullOrWhiteSpace(body.Text))
                {
                    var prompt = new QueuedPrompt(body.Text, [], body.ReplyToRowId, body.WireId);
                    if (string.Equals(body.Command, "enqueue", StringComparison.Ordinal))
                    {
                        control.Enqueue(prompt);
                    }
                    else
                    {
                        await control.SubmitAsync(prompt, body.TakesMidTurnInput);
                    }

                    answer = Results.Json(new { paneId, prompt.WireId });
                    return;
                }

                answer = BackendApiRoutes.Error(
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "command must be submit, enqueue, withdraw, or clear; submit and enqueue require text.");
            }).ConfigureAwait(false);

            return used ? answer ?? Results.Empty : Results.NotFound();
        }).RequireOperate().Audited("change_session_queue", services);

        api.MapGet("/assistant/transcript", async (int? count) =>
        {
            if (sessions().Assistant is not { } assistant)
            {
                return Results.NotFound();
            }

            var slice = await assistant.ReadTranscriptAsync(_Count(count)).ConfigureAwait(false);
            var snapshot = await assistant.ReadRowsAtAsync(() => log().LastSeq).ConfigureAwait(false);
            return Results.Json(new
            {
                paneId = assistant.PaneId,
                name = assistant.Title,
                totalEntries = slice.TotalEntries,
                entries = slice.Entries.Select(entry => new { kind = entry.Kind, text = entry.Text, toolResult = entry.ToolResult }),
                rows = snapshot?.Rows,
                seq = snapshot?.Seq,
            });
        }).RequireOperate().ForAssistantHolderOnly();

        // Held until the assistant can take it, as a node prompt is; the door has already kept this from holding it.
        api.MapPost("/assistant/prompt", async (PromptBody body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text))
            {
                return _TextRequired();
            }

            if (sessions().Assistant is not { } assistant)
            {
                return Results.NotFound();
            }

            return await assistant.SubmitPromptWhenReadyAsync(body.Text).ConfigureAwait(false) is { } delivered
                ? Results.Json(new { paneId = assistant.PaneId, delivered })
                : BackendApiRoutes.Error(StatusCodes.Status409Conflict, "not_delivered", "The assistant is still starting and already has a turn waiting; this one was not accepted.");
        }).RequireOperate().Audited("assistant_prompt", services).ForAssistantHolderOnly();
    }

    // AC-1479: the assistant's conversation is for the key that holds it (AC-1366); another key, whatever its capability,
    // gets the door's 403. After `Audited`, so the refusal is in the audit.
    private static RouteHandlerBuilder ForAssistantHolderOnly(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (context, next) =>
            McpRequestContext.CurrentNodeCaller is { HoldsAssistant: true }
                ? await next(context).ConfigureAwait(false)
                : BackendApiRoutes.Error(StatusCodes.Status403Forbidden, "forbidden", "This key does not hold the assistant."));

    // Every mutating route into the node access audit as `api:<name>`, as the node tools are (AC-1351).
    internal static RouteHandlerBuilder Audited(this RouteHandlerBuilder route, string name, IServiceProvider services) =>
        route.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context).ConfigureAwait(false);
            if (services.GetService<NodeAccessAuditLog>() is { } audit && McpRequestContext.CurrentNodeCaller is { } caller)
            {
                var outcome = result is IStatusCodeHttpResult { StatusCode: >= 400 } ? "refused" : "called";
                await audit.RecordAsync(
                    NodeAccessAuditEntry.By(caller, DateTimeOffset.UtcNow, $"api:{name}", outcome),
                    context.HttpContext.RequestAborted).ConfigureAwait(false);
            }

            return result;
        });

    private static IResult _TextRequired() =>
        BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "text is required.");

    private static bool _IsStructuredAnswer(string answersJson)
    {
        try
        {
            using var answers = JsonDocument.Parse(answersJson);
            return answers.RootElement.ValueKind == JsonValueKind.Object
                && answers.RootElement.EnumerateObject().All(answer => answer.Value.ValueKind == JsonValueKind.String);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<ConversationRecord>> _VisibleConversationsAsync(IServiceProvider services, NodeCaller caller, CancellationToken cancellationToken)
    {
        var reachable = await NodeCallerSessionPolicy.ReachableProfilesAsync(caller,
            services.GetRequiredService<ISessionProfileStore>(), services.GetService<IProfileLoginChecker>()).ConfigureAwait(false);
        var pairing = services.GetRequiredService<INodePairingBroker>();
        var records = await services.GetRequiredService<IConversationIndex>().LoadAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Where(record => !record.IsAssistant && record.TranscriptPath is not null && record.ConversationState == SessionConversationIdState.Known
                && !string.IsNullOrWhiteSpace(record.ConversationId) && record.ProfileId is { } profile
                && reachable(profile) && caller.AllowsSession(profile, record.ProjectId, pairing))
            .OrderByDescending(record => record.LastActivityAt)];
    }

    private static int _Count(int? count) =>
        Math.Clamp(count ?? AssistantReadMcpTools.DefaultEntryCount, 1, AssistantReadMcpTools.MaxEntryCount);

    internal static string ProviderId(SessionProfile profile) =>
        profile.ProviderConfig is PluginProviderConfig plugin
            ? plugin.ProviderId
            : profile.Provider == SessionProvider.ClaudeCli
                ? ClaudePluginProfile.ProviderId
                : profile.Provider.ToString().ToLowerInvariant();

    private static RemoteProviderUsageSignal _UsageSignal(Cockpit.Plugins.Abstractions.Sessions.PluginUsageSignal signal) => new(
        signal.Key,
        signal.Label,
        signal.Kind.ToString().ToLowerInvariant(),
        signal.DefaultThresholdPercent,
        signal.Description,
        signal.SupportsResume,
        signal.DefaultResumePrompt);

    // Only reached behind the group's door, which has checked the caller is there.
    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A session route ran without a connect-key caller.");
}

internal sealed record StartSessionBody(string Profile, string? ProjectId, string? Prompt, string? Name, string? Role = null, string? WorkspaceId = null, string? Kind = null, SessionResume? Resume = null, string? PaneId = null);

internal sealed record PromptBody(string Text);

internal sealed record PermissionBody(bool Allow, string? Scope = null, string? AnswersJson = null);

internal sealed record SessionNameBody(string Name);

internal sealed record VerifyResultBody(string Caption, byte[] ScreenshotPng);

internal sealed record ModelBody(string? Model);

internal sealed record PermissionModeBody(string Mode);

internal sealed record QueueCommandBody(string Command, string? WireId, string? Text, string? ReplyToRowId, bool TakesMidTurnInput = false);

internal sealed record McpSetChangeBody(string Name, bool Add);
