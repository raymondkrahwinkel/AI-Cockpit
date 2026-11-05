using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Workspaces;
using Cockpit.App.Services;

namespace Cockpit.App.ViewModels;

public partial class CockpitViewModel
{
    private SimplePage _simpleWorkspaceReturnPage;
    private SessionPanelViewModel? _simpleWorkspaceReturnSession;

    public bool SimpleStandShowsTheWorkspace => SimpleView && SimplePage == SimplePage.Workspace;

    public bool ShowWorkspaceContent => !SimpleView || SimpleStandShowsTheWorkspace;

    private void _ShowSimpleWorkspace()
    {
        if (!SimpleView)
        {
            return;
        }
        if (SimplePage != SimplePage.Workspace)
        {
            _simpleWorkspaceReturnPage = SimplePage;
            _simpleWorkspaceReturnSession = SimpleSelectedSession;
        }
        SimplePage = Workspaces.Active?.Type == WorkspaceType.Projects ? SimplePage.Projects
            : Workspaces.IsSessionsActive ? SimplePage.Conversation : SimplePage.Workspace;
        SimpleOpenDockPanelId = null;
    }

    [RelayCommand]
    private async Task ShowSimpleWorkspaceAsync(string workspaceId)
    {
        await Workspaces.SelectWorkspaceCommand.ExecuteAsync(workspaceId);
        _ShowSimpleWorkspace();
    }

    private Task<bool> _CanLeaveSimpleWorkspaceAsync() =>
        SimpleStandShowsTheWorkspace && Workspaces.Active is { } workspace
            ? Workspaces.ConfirmCloseAsync(workspace.Id) : Task.FromResult(true);

    [RelayCommand]
    private async Task ReturnFromSimpleWorkspaceAsync()
    {
        if (await _CanLeaveSimpleWorkspaceAsync())
        {
            await _RestoreSimplePageAsync();
        }
    }

    private async Task _RestoreSimplePageAsync()
    {
        var session = _simpleWorkspaceReturnSession;
        SimpleSelectedSession = session is not null && (Sessions.Contains(session) || ReferenceEquals(session, AssistantRootSession)) ? session : null;
        SimplePage = _simpleWorkspaceReturnPage == SimplePage.Conversation && SimpleSelectedSession is null
            ? SimplePage.Home : _simpleWorkspaceReturnPage;
        if (SimpleSelectedSession is { } selected && SessionWorkspacePlacement.Resolve(selected, SessionWorkspacePlacement.FirstSessionsWorkspaceId(Workspaces.Settings)) is { } workspaceId)
        {
            await Workspaces.SelectWorkspaceCommand.ExecuteAsync(workspaceId);
        }
    }
}
