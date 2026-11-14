using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Toasts;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewModels;

public sealed partial class WorkspacesViewModel
{
    private Func<ISessionLauncher?>? _remoteLauncher;
    private readonly SemaphoreSlim _remoteChanges = new(1, 1);

    internal void UseRemoteWorkspaces(Func<ISessionLauncher?> launcher) => _remoteLauncher = launcher;

    internal void ReconcileRemoteWorkspaces(WorkspaceSettings? presentation = null)
    {
        if (_remoteLauncher?.Invoke() is { } launcher)
        {
            presentation ??= Settings;
            var snapshot = launcher.Workspaces;
            Settings = snapshot with
            {
                CollapsedSidebarWorkspaceIds = [.. presentation.CollapsedSidebarWorkspaceIds.Where(id => snapshot.Workspaces.Any(workspace => workspace.Id == id))],
                Workspaces = [.. snapshot.Workspaces.Select(workspace => workspace with
                {
                    IsFloating = presentation.Workspaces.FirstOrDefault(previous => previous.Id == workspace.Id)?.IsFloating == true,
                })],
            };
        }
    }

    private async Task _ApplyRemoteAsync(WorkspaceSettings before, WorkspaceSettings proposed)
    {
        await _remoteChanges.WaitAsync();
        try
        {
            if (_remoteLauncher?.Invoke() is not IWorkspaceCommands commands)
            {
                throw new InvalidOperationException("The server is not connected.");
            }

            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var workspace in proposed.Workspaces.Where(workspace => before.Workspaces.All(previous => previous.Id != workspace.Id)))
            {
                var created = await commands.CreateWorkspaceAsync(workspace);
                ids.Add(workspace.Id, created.Id);
            }
            foreach (var workspace in before.Workspaces.Where(workspace => proposed.Workspaces.All(next => next.Id != workspace.Id)))
            {
                if (await commands.CloseWorkspaceIfEmptyAsync(workspace.Id) != 0)
                {
                    throw new InvalidOperationException("Move or stop the sessions before closing this workspace.");
                }
            }
            foreach (var workspace in proposed.Workspaces)
            {
                if (before.Workspaces.FirstOrDefault(previous => previous.Id == workspace.Id) is not { } previous || previous == workspace)
                {
                    continue;
                }
                if (workspace.Type == WorkspaceType.Sessions)
                {
                    foreach (var pane in workspace.Panes.Where(pane => previous.Panes.All(existing => existing.Id != pane.Id)))
                    {
                        await commands.MoveSessionPaneAsync(pane.Id, workspace.Id);
                    }
                }
                if (previous.Name != workspace.Name)
                {
                    await commands.RenameWorkspaceAsync(workspace.Id, workspace.Name);
                }
                if (previous with { Name = workspace.Name, Panes = workspace.Panes, IsFloating = workspace.IsFloating } != workspace)
                {
                    await commands.UpdateWorkspaceAsync(workspace);
                }
                else if (workspace.Type == WorkspaceType.Dashboard)
                {
                    await commands.UpdateWorkspaceAsync(workspace);
                }
            }
            if (before.Workspaces.Count == proposed.Workspaces.Count
                && !before.Workspaces.Select(workspace => workspace.Id).SequenceEqual(proposed.Workspaces.Select(workspace => workspace.Id)))
            {
                for (var index = 0; index < proposed.Workspaces.Count; index++)
                {
                    await commands.MoveWorkspaceAsync(proposed.Workspaces[index].Id, index);
                }
            }
            if (before.ActiveWorkspaceId != proposed.ActiveWorkspaceId && proposed.ActiveWorkspaceId is { } active)
            {
                await commands.SelectWorkspaceAsync(ids.GetValueOrDefault(active, active));
            }
        }
        catch (Exception exception)
        {
            _toasts?.Add($"This workspace change could not be saved: {exception.Message}", ToastSeverity.Error, null, null);
        }
        finally
        {
            ReconcileRemoteWorkspaces(proposed);
            _remoteChanges.Release();
        }
    }
}
