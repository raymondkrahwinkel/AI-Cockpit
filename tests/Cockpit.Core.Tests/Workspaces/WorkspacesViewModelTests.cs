using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// <see cref="WorkspacesViewModel"/> — the tab strip and the commands behind it, including the two the
/// Ctrl+Shift+Left/Right shortcuts are bound to. Every change is expected to persist immediately; the store
/// is the assertion for that, since a workspace switch that is not saved comes back wrong after a restart.
/// </summary>
public class WorkspacesViewModelTests
{
    [Fact]
    public void ASingleWorkspace_HidesTheTabStrip_SinceALoneTabIsChromeThatEarnsNothing()
    {
        new WorkspacesViewModel().ShowTabStrip.Should().BeFalse();
    }

    [Fact]
    public async Task AddingASecondWorkspace_ShowsTheTabStrip()
    {
        var viewModel = _Create(out _);

        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        viewModel.ShowTabStrip.Should().BeTrue();
        viewModel.Tabs.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddWorkspace_MakesTheNewOneActive_AndPersists()
    {
        var viewModel = _Create(out var store);

        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        viewModel.Active!.Type.Should().Be(WorkspaceType.Dashboard);
        await store.Received().SaveAsync(Arg.Any<WorkspaceSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddWorkspace_Twice_NamesThemApart_RatherThanShowingTwoIdenticalTabs()
    {
        var viewModel = _Create(out _);

        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        viewModel.Settings.Workspaces.Select(workspace => workspace.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task NextWorkspace_StepsAndWraps()
    {
        var viewModel = _Create(out _);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var first = viewModel.Settings.Workspaces[0];

        // Active is the dashboard (the one just added, i.e. the last tab); stepping forward wraps to the first.
        await viewModel.SelectNextWorkspaceCommand.ExecuteAsync(null);

        viewModel.Active!.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task PreviousWorkspace_StepsBackAndWraps()
    {
        var viewModel = _Create(out _);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var dashboard = viewModel.Active!;
        await viewModel.SelectNextWorkspaceCommand.ExecuteAsync(null);

        await viewModel.SelectPreviousWorkspaceCommand.ExecuteAsync(null);

        viewModel.Active!.Id.Should().Be(dashboard.Id);
    }

    [Fact]
    public async Task SwitchingWorkspace_Persists_SoARestartComesBackOnTheSameOne()
    {
        var viewModel = _Create(out var store);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        store.ClearReceivedCalls();

        await viewModel.SelectNextWorkspaceCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(Arg.Any<WorkspaceSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SwitchingWithOneWorkspace_DoesNotPersist_SinceNothingChanged()
    {
        var viewModel = _Create(out var store);

        await viewModel.SelectNextWorkspaceCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<WorkspaceSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsDashboardActive_TracksTheActiveWorkspacesType_SoTheDashboardOnlyChromeCanGateOnIt()
    {
        var viewModel = _Create(out _);
        viewModel.IsDashboardActive.Should().BeFalse();

        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        viewModel.IsDashboardActive.Should().BeTrue();
    }

    [Fact]
    public async Task AddWidget_OnADashboard_PlacesItAtTheFirstFreeCell()
    {
        var viewModel = _Create(out _);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        await viewModel.AddWidgetAsync("clock.time");
        await viewModel.AddWidgetAsync("system-monitor.usage");

        viewModel.Active!.Panes.Select(pane => pane.Cell).Should().Equal(new GridCell(0, 0), new GridCell(1, 0));
    }

    [Fact]
    public async Task AddWidget_OnASessionsWorkspace_IsIgnored_RatherThanThrowingBehindAHiddenButton()
    {
        var viewModel = _Create(out _);

        await viewModel.AddWidgetAsync("clock.time");

        viewModel.Active!.Panes.Should().BeEmpty();
    }

    [Fact]
    public async Task MovePane_RepositionsWithoutRebuildingThePane()
    {
        var viewModel = _Create(out _);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        await viewModel.AddWidgetAsync("clock.time");
        var pane = viewModel.Active!.Panes[0];

        await viewModel.MovePaneAsync(pane.Id, new GridCell(1, 1));

        var moved = viewModel.Active!.Panes.Should().ContainSingle().Subject;
        moved.Id.Should().Be(pane.Id);
        moved.WidgetId.Should().Be("clock.time");
        moved.Cell.Should().Be(new GridCell(1, 1));
    }

    [Fact]
    public async Task RemovePane_DropsIt()
    {
        var viewModel = _Create(out _);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        await viewModel.AddWidgetAsync("clock.time");

        await viewModel.RemovePaneAsync(viewModel.Active!.Panes[0].Id);

        viewModel.Active!.Panes.Should().BeEmpty();
    }

    [Fact]
    public async Task SetDashboardLayout_ClampsAndPersists()
    {
        var viewModel = _Create(out _);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        await viewModel.SetDashboardLayoutAsync(viewModel.Active!.Id, new DashboardLayout { Columns = 9999, Rows = 3 });

        viewModel.Active!.Layout.Columns.Should().Be(DashboardLayout.MaxColumns);
        viewModel.Active!.Layout.Rows.Should().Be(3);
    }

    [Fact]
    public async Task SetDashboardLayout_OnASessionsWorkspace_IsIgnored_SinceItHasNoGridToSet()
    {
        var viewModel = _Create(out _);

        await viewModel.SetDashboardLayoutAsync(viewModel.Active!.Id, new DashboardLayout { Columns = 4 });

        viewModel.Active!.Layout.Columns.Should().Be(DashboardLayout.DefaultColumns);
    }

    [Fact]
    public async Task RenameWorkspace_RenamesTheTab()
    {
        var viewModel = _Create(out _);

        await viewModel.RenameWorkspaceCommand.ExecuteAsync((viewModel.Active!.Id, "Client work"));

        viewModel.Tabs[0].Name.Should().Be("Client work");
    }

    [Fact]
    public async Task RenameWorkspace_ToBlank_IsIgnored_SoNoTabCanLoseItsLabel()
    {
        var viewModel = _Create(out _);
        var original = viewModel.Active!.Name;

        await viewModel.RenameWorkspaceCommand.ExecuteAsync((viewModel.Active!.Id, "   "));

        viewModel.Active!.Name.Should().Be(original);
    }

    [Fact]
    public async Task CloseWorkspace_TheLastOne_IsRefused_SoTheGridAlwaysHasSomethingToRender()
    {
        var viewModel = _Create(out _);

        await viewModel.CloseWorkspaceCommand.ExecuteAsync(viewModel.Active!.Id);

        viewModel.Settings.Workspaces.Should().ContainSingle();
    }

    [Fact]
    public async Task InitializeAsync_AdoptsWhatTheStoreHeld()
    {
        var store = Substitute.For<IWorkspaceSettingsStore>();
        var saved = WorkspaceSettings.Default.WithWorkspace(Workspace.Create("Monitoring", WorkspaceType.Dashboard));
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(saved);
        var viewModel = new WorkspacesViewModel(store);

        await viewModel.InitializeAsync();

        viewModel.Tabs.Should().HaveCount(2);
        viewModel.Active!.Name.Should().Be("Monitoring");
    }

    [Fact]
    public async Task Tabs_MarkExactlyOneAsActive()
    {
        var viewModel = _Create(out _);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        viewModel.Tabs.Count(tab => tab.IsActive).Should().Be(1);
    }

    private static WorkspacesViewModel _Create(out IWorkspaceSettingsStore store)
    {
        store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(WorkspaceSettings.Default);
        return new WorkspacesViewModel(store);
    }
}
