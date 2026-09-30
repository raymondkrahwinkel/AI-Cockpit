using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using NSubstitute;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// <see cref="WorkspacesViewModel"/> — the tab strip and the commands behind it, including the two the
/// Ctrl+Shift+Left/Right shortcuts are bound to. Every change is expected to persist immediately; the store
/// is the assertion for that, since a workspace switch that is not saved comes back wrong after a restart.
/// </summary>
public class WorkspacesViewModelTests
{
    /// <summary>
    /// The half that matters. A failed load leaves Settings on the constructor's default, and every change here
    /// persists the whole of it — so the first thing the operator touched would write that default over the
    /// workspaces they actually have. Not "their dashboards are invisible this session": their dashboards are
    /// gone.
    /// </summary>
    [Fact]
    public async Task WhenTheSavedWorkspacesCouldNotBeRead_NoLaterChangeWritesTheDefaultOverThem()
    {
        var store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns<WorkspaceSettings>(_ => throw new IOException("cockpit.json is being used by another process"));
        var viewModel = new WorkspacesViewModel(store, widgets: null, new ToastHostViewModel((_, _) => { }));
        await viewModel.InitializeAsync();

        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        await viewModel.SelectPreviousWorkspaceCommand.ExecuteAsync(null);

        await store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

}
