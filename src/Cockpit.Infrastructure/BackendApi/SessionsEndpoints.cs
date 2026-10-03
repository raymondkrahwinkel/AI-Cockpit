using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
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
        NodeCallerSessionPolicy policy() => new(read(), gateway(), services.GetRequiredService<INodePairingBroker>(), services.GetRequiredService<ISessionProfileStore>());

        // AC-1388: `seq` is read before the list, so a reader following the stream after it misses no change to it.
        api.MapGet("/sessions", async () =>
        {
            var seq = log().LastSeq;
            var visible = await policy().VisibleSessionsAsync(_Caller()).ConfigureAwait(false);
            var pending = (await read().ListPendingPermissionsAsync().ConfigureAwait(false)).ToLookup(permission => permission.PaneId, StringComparer.Ordinal);
            return Results.Json(new
            {
                seq,
                assistant = sessions().Assistant is { } assistant ? new { paneId = assistant.PaneId, name = assistant.Title } : null,
                sessions = visible.Select(session => new
                {
                    paneId = session.PaneId,
                    name = session.Name,
                    profile = session.Profile,
                    projectId = session.ProjectId,
                    statusline = session.Statusline,
                    status = session.Status,
                    needsYou = session.NeedsYou,
                    hasOutstandingWork = session.HasOutstandingWork,
                    pendingPermissions = pending[session.PaneId].Select(permission => new
                    {
                        toolUseId = permission.ToolUseId,
                        tool = permission.ToolName,
                        input = permission.InputJson,
                        sinceUtc = permission.SinceUtc,
                    }),
                }),
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

        api.MapPost("/sessions", async (StartSessionBody body) =>
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

            if (await policy().ActiveWorkspaceIdAsync().ConfigureAwait(false) is not { } workspaceId)
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, NotStarted, "This node has no desk that can hold a session just now.");
            }

            // The node tool's spawn: the desk read here, the profile as this machine spells it, and no folder or
            // provider options from the caller.
            var result = await gateway().SpawnAsync(new AgentSpawnRequest(
                SpawnTarget.RequestedByThePairedController(workspaceId),
                allowedProfile.Label,
                body.ProjectId,
                body.Prompt,
                WorkingDirectory: null,
                SessionName: body.Name)).ConfigureAwait(false);

            return result.Ok
                ? Results.Json(new
                {
                    paneId = result.PaneId,
                    name = result.SessionName,
                    resolvedProfile = result.ResolvedProfileLabel,
                    promptDelivered = result.PromptDelivered,
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

            var answered = await gateway().RespondToPermissionAsync(paneId, toolUseId, body.Allow).ConfigureAwait(false);
            return Results.Json(new { paneId, toolUseId, answered });
        }).RequireOperate().Audited("answer_permission", services);

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
        }).RequireOperate();

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
        }).RequireOperate().Audited("assistant_prompt", services);
    }

    // Every mutating route into the node access audit as `api:<name>`, as the node tools are (AC-1351).
    private static RouteHandlerBuilder Audited(this RouteHandlerBuilder route, string name, IServiceProvider services) =>
        route.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context).ConfigureAwait(false);
            if (services.GetService<NodeAccessAuditLog>() is { } audit && McpRequestContext.CurrentNodeCaller is { } caller)
            {
                var outcome = result is IStatusCodeHttpResult { StatusCode: >= 400 } ? "refused" : "called";
                await audit.RecordAsync(
                    new NodeAccessAuditEntry(DateTimeOffset.UtcNow, caller.Credential, caller.KeyPrefix, caller.RemoteAddress, $"api:{name}", outcome),
                    context.HttpContext.RequestAborted).ConfigureAwait(false);
            }

            return result;
        });

    private static IResult _TextRequired() =>
        BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "text is required.");

    private static int _Count(int? count) =>
        Math.Clamp(count ?? AssistantReadMcpTools.DefaultEntryCount, 1, AssistantReadMcpTools.MaxEntryCount);

    // Only reached behind the group's door, which has checked the caller is there.
    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A session route ran without a connect-key caller.");
}

internal sealed record StartSessionBody(string Profile, string? ProjectId, string? Prompt, string? Name);

internal sealed record PromptBody(string Text);

internal sealed record PermissionBody(bool Allow);
