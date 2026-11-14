using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

internal static class WorkspacesEndpoints
{
    internal const string EventKind = "workspaces-changed";

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        ISessionDesks desks() => services.GetRequiredService<ISessionDesks>();
        IWorkspaceCommands commands() => (IWorkspaceCommands)desks();
        NodeCallerSessionPolicy policy() => new(services.GetRequiredService<IAssistantReadGateway>(),
            services.GetRequiredService<IAssistantAgentGateway>(), services.GetRequiredService<INodePairingBroker>(),
            services.GetRequiredService<ISessionProfileStore>(), services.GetService<IProfileLoginChecker>());
        NodeCaller caller() => McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A workspace route ran without a caller.");

        api.MapGet("/workspaces", async () =>
        {
            var visible = (await policy().VisibleSessionsAsync(caller())).Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal);
            return Results.Json(await desks().RunExclusiveAsync(() => View(desks().Workspaces, visible)));
        }).RequireAdmin();
        api.MapPost("/workspaces", async (Workspace workspace) =>
        {
            if (!_Valid(workspace) || workspace.Type == WorkspaceType.Projects)
            {
                return BackendApiRoutes.Error(400, "invalid_workspace", "A name and a workspace type other than Projects are required.");
            }
            var created = await commands().CreateWorkspaceAsync(workspace);
            return Results.Json(created with { Panes = [] }, statusCode: 201);
        }).RequireAdmin().Audited("create_workspace", services);
        api.MapPost("/workspaces/{workspaceId}/rename", async (string workspaceId, WorkspaceName body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return BackendApiRoutes.Error(400, "invalid_workspace", "A workspace name is required.");
            }
            if (!await desks().RunExclusiveAsync(() => desks().Workspaces.Workspaces.Any(workspace => workspace.Id == workspaceId)))
            {
                return Results.NotFound();
            }
            await commands().RenameWorkspaceAsync(workspaceId, body.Name);
            return Results.Json(new { });
        }).RequireAdmin().Audited("rename_workspace", services);
        api.MapPut("/workspaces/{workspaceId}", async (string workspaceId, Workspace workspace) =>
        {
            if (!_Valid(workspace) || workspace.Id != workspaceId || workspace.Panes.Any(pane => pane.Kind != PaneKind.Widget))
            {
                return BackendApiRoutes.Error(400, "invalid_workspace", "Only workspace presentation and widget placement may be updated here.");
            }
            if (!await desks().RunExclusiveAsync(() => desks().Workspaces.Workspaces.Any(existing => existing.Id == workspaceId)))
            {
                return Results.NotFound();
            }
            await commands().UpdateWorkspaceAsync(workspace);
            return Results.Json(new { });
        }).RequireAdmin().Audited("update_workspace", services);
        api.MapPost("/workspaces/{workspaceId}/select", async (string workspaceId) =>
        {
            if (!await desks().RunExclusiveAsync(() => desks().Workspaces.Workspaces.Any(workspace => workspace.Id == workspaceId)))
            {
                return Results.NotFound();
            }
            await commands().SelectWorkspaceAsync(workspaceId);
            return Results.Json(new { });
        }).RequireAdmin().Audited("select_workspace", services);
        api.MapDelete("/workspaces/{workspaceId}", async (string workspaceId) =>
        {
            if (!await desks().RunExclusiveAsync(() => desks().CanCloseWorkspace(workspaceId)))
            {
                return BackendApiRoutes.Error(409, "workspace_required", "This workspace cannot be closed.");
            }
            return await commands().CloseWorkspaceIfEmptyAsync(workspaceId) == 0
                ? Results.Json(new { })
                : BackendApiRoutes.Error(409, "workspace_occupied", "Move or stop the sessions before closing this workspace.");
        }).RequireAdmin().Audited("close_workspace", services);
        api.MapPut("/workspaces/{workspaceId}/position", async (string workspaceId, WorkspacePosition body) =>
        {
            await commands().MoveWorkspaceAsync(workspaceId, body.Index);
            return Results.Json(new { });
        }).RequireAdmin().Audited("move_workspace", services);
        api.MapPut("/sessions/{paneId}/workspace", async (string paneId, WorkspaceTarget body) =>
        {
            if (!await policy().IsVisibleAsync(caller(), paneId))
            {
                return Results.NotFound();
            }
            return await commands().MoveSessionPaneAsync(paneId, body.WorkspaceId)
                ? Results.Json(new { })
                : BackendApiRoutes.Error(409, "placement_refused", "The session or its target workspace is no longer available.");
        })
            .RequireAdmin().Audited("move_session_workspace", services);
    }

    internal static WorkspaceSettings View(WorkspaceSettings settings, IReadOnlySet<string> visiblePaneIds) => settings with
    {
        Workspaces = [.. settings.Workspaces.Select(workspace => workspace with
        {
            IsFloating = false,
            Panes = [.. workspace.Panes.Where(pane => pane.Kind == PaneKind.Widget || visiblePaneIds.Contains(pane.Id))
                .Select(pane => new WorkspacePane(pane.Id, pane.Kind) { Cell = pane.Cell, WidgetId = pane.WidgetId })],
        })],
    };

    private static bool _Valid(Workspace workspace) =>
        !string.IsNullOrWhiteSpace(workspace.Name) && !string.IsNullOrWhiteSpace(workspace.Type.Id)
        && workspace.Layout is not null && workspace.Panes is not null;

    private sealed record WorkspacePosition(int Index);
    private sealed record WorkspaceTarget(string WorkspaceId);
    private sealed record WorkspaceName(string Name);
}
