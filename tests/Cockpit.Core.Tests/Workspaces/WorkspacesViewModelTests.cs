using System.Text.Json;
using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Toasts;
using Cockpit.Core.Workspaces;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;
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
    public void TheTabStrip_IsShownEvenForASingleWorkspace()
    {
        // It used to hide itself at one workspace, which Raymond found from both sides: deleting one of two made
        // the strip vanish, so a correct single deletion looked like it took both; and the workspace that was
        // there all along reappeared out of nowhere when a second one arrived. A tab says which desk you are on,
        // and it has to keep saying so when there is one.
        new WorkspacesViewModel().ShowTabStrip.Should().BeTrue();
    }

    [Fact]
    public async Task DeletingOneOfTwo_LeavesTheOtherVisible_RatherThanHidingTheWholeStrip()
    {
        var viewModel = _Create(out _);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var dashboard = viewModel.Active!;

        await viewModel.CloseWorkspaceCommand.ExecuteAsync(dashboard.Id);

        viewModel.Tabs.Should().ContainSingle();
        viewModel.ShowTabStrip.Should().BeTrue("the survivor must stay on screen — otherwise one deletion reads as two");
    }

    [Fact]
    public async Task AddingASecondWorkspace_ShowsBothTabs()
    {
        var viewModel = _Create(out _);

        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        viewModel.Tabs.Should().HaveCount(2);
    }

    [Fact]
    public void EnsureSessionWorkspace_WithOneShowing_UsesIt()
    {
        var viewModel = _Create(out _);

        viewModel.EnsureSessionWorkspace().Should().Be(viewModel.Active!.Id);
    }

    [Fact]
    public async Task EnsureSessionWorkspace_WhileADashboardIsShowing_SwitchesToTheSessionsOne()
    {
        var viewModel = _Create(out _);
        var sessions = viewModel.Active!;
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        viewModel.EnsureSessionWorkspace().Should().Be(sessions.Id);
        viewModel.Active!.Id.Should().Be(sessions.Id, "the session has to appear where it was put");
    }

    [Fact]
    public async Task EnsureSessionWorkspace_WithNoSessionsWorkspaceAtAll_CreatesOne()
    {
        // A session started onto a dashboard would run invisibly, which is worse than not starting it.
        var viewModel = _Create(out _);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        await viewModel.CloseWorkspaceCommand.ExecuteAsync(viewModel.Settings.Workspaces[0].Id);
        viewModel.Settings.Workspaces.Should().ContainSingle().Which.Type.Should().Be(WorkspaceType.Dashboard);

        var created = viewModel.EnsureSessionWorkspace();

        viewModel.Settings.Workspaces.Single(workspace => workspace.Id == created).Type.Should().Be(WorkspaceType.Sessions);
        viewModel.Active!.Id.Should().Be(created);
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

    /// <summary>
    /// Every change here is fire-and-forget (<c>_ = _ApplyAsync(…)</c>), so a throwing save used to land on a task
    /// nobody observes and simply be gone. It is not a hypothetical throw: the write goes through
    /// <c>CockpitConfigFileAccess.UpdateAsync</c>, which refuses rather than writes when the config's write gate
    /// times out or the file will not read. Silence there leaves a workspace on screen that is not on disk, and
    /// the operator finds out at the next start with no reason given.
    /// </summary>
    [Fact]
    public async Task AChangeThatCannotBeSaved_IsSaidOutLoud_RatherThanLostOnATaskNobodyObserves()
    {
        var store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(WorkspaceSettings.Default);
        store.SaveAsync(Arg.Any<WorkspaceSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("the write lock could not be taken")));

        var toasts = new ToastHostViewModel((_, _) => { });
        var viewModel = new WorkspacesViewModel(store, widgets: null, toasts);

        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        toasts.Toasts.Should().ContainSingle().Which.Severity.Should().Be(ToastSeverity.Error);
        toasts.Toasts[0].Message.Should().Contain("the write lock could not be taken", "the reason is the point of saying anything at all");
    }

    [Fact]
    public async Task AFailedSave_DoesNotThrowOutOfTheCommand()
    {
        var store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(WorkspaceSettings.Default);
        store.SaveAsync(Arg.Any<WorkspaceSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("nope")));

        // No toast host, the way the design-time and unit-test graphs are built: having nowhere to report a
        // failed save must not turn it into a crash.
        var viewModel = new WorkspacesViewModel(store);

        var act = async () => await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);

        await act.Should().NotThrowAsync();
        viewModel.Tabs.Should().HaveCount(2, "what the operator did is still on screen");
    }

    [Fact]
    public async Task ImportDashboard_PlacesTheWidgetsTheFileNames()
    {
        var viewModel = _CreateWithWidget();

        var import = await viewModel.ImportDashboardAsync(_ExportJson("{\"ShowCpu\":true}"));

        import.Should().NotBeNull();
        viewModel.Tabs.Should().HaveCount(2);
        viewModel.Active!.Name.Should().Be("Monitoring");
        viewModel.Active.Panes.Should().ContainSingle().Which.WidgetId.Should().Be("w");
    }

    /// <summary>
    /// The envelope reads and the settings do not. The workspace used to be applied first and the settings parsed
    /// second, so the JsonException escaped this method entirely — past its own catch, which only ever covered the
    /// envelope — and left the dashboard on the strip with its widgets unconfigured. A file this build cannot read
    /// has to be said, not thrown, and nothing of it may land.
    /// </summary>
    [Fact]
    public async Task ImportDashboard_WithSettingsThatDoNotParse_LandsNothing_RatherThanHalfADashboard()
    {
        var viewModel = _CreateWithWidget();

        var import = await viewModel.ImportDashboardAsync(_ExportJson("{not json at all"));

        import.Should().BeNull();
        viewModel.Tabs.Should().ContainSingle("a file that cannot be read must not leave a dashboard behind");
        viewModel.Settings.Workspaces.Should().ContainSingle();
    }

    /// <summary>A dashboard export carrying one widget of type "w", whose only setting holds <paramref name="configValue"/> verbatim.</summary>
    private static string _ExportJson(string configValue) =>
        JsonSerializer.Serialize(new DashboardExport(
            DashboardExport.CurrentFormatVersion,
            "Monitoring",
            new DashboardLayout { Columns = 2, Rows = 2 },
            [new DashboardExportPane("w", new GridCell(0, 0), new Dictionary<string, string> { ["metrics"] = configValue })]));

    private static WorkspacesViewModel _CreateWithWidget()
    {
        var store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(WorkspaceSettings.Default);

        var registry = new WidgetRegistry();
        registry.Register(
            new WidgetRegistration("w", "W", _ => new TextBlock()),
            Substitute.For<IPluginStorage>(),
            Substitute.For<ICockpitSessionObserver>(),
            []);

        return new WorkspacesViewModel(store, registry);
    }

    private static WorkspacesViewModel _Create(out IWorkspaceSettingsStore store)
    {
        store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(WorkspaceSettings.Default);
        return new WorkspacesViewModel(store);
    }
}
