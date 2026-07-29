using System.Text.Json;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// <see cref="WorkspaceSettingsStore"/> against a real config file: the round trip, that it leaves sibling
/// sections alone, and what it does with a file that disagrees with itself. The recovery cases matter more
/// than the happy path — a malformed <c>workspaces</c> section must not cost the operator their cockpit.
/// </summary>
public class WorkspaceSettingsStoreTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"cockpit-workspaces-{Guid.NewGuid():n}.json");

    public void Dispose()
    {
        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LoadAsync_NothingEverSaved_YieldsTheDefaultSessionsWorkspacePlusTheFixedOverview()
    {
        var settings = await new WorkspaceSettingsStore(_configPath).LoadAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(settings.Workspaces));
        Assert.Single(settings.Workspaces, workspace => workspace.Type == WorkspaceType.Sessions);
        Assert.Single(settings.Workspaces, workspace => workspace.Type == WorkspaceType.Projects);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsWorkspacesPanesAndTheActiveOne()
    {
        var store = new WorkspaceSettingsStore(_configPath);
        var dashboard = Workspace.Create("Monitoring", WorkspaceType.Dashboard) with { Layout = new DashboardLayout { Columns = 3, Rows = 2 } };
        dashboard = dashboard.WithPane(new WorkspacePane("p1", PaneKind.Widget)
        {
            WidgetId = "system-monitor.usage",
            Cell = new GridCell(1, 0, 2, 1),
        });
        var saved = WorkspaceSettings.Default.WithWorkspace(dashboard);

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        Assert.Equal(3, System.Linq.Enumerable.Count(loaded.Workspaces));
        Assert.Equal(dashboard.Id, loaded.ActiveWorkspaceId);
        var reloaded = loaded.Workspaces.Single(workspace => workspace.Id == dashboard.Id);
        Assert.Equal("Monitoring", reloaded.Name);
        Assert.Equal(3, reloaded.Layout.Columns);
        Assert.Equivalent(dashboard.Panes[0], Assert.Single(reloaded.Panes));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheGridLinesToggle_SoItSurvivesARestart()
    {
        var store = new WorkspaceSettingsStore(_configPath);
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard) with { Layout = new DashboardLayout { ShowGridLines = true } };

        await store.SaveAsync(WorkspaceSettings.Default.WithWorkspace(dashboard));
        var loaded = await store.LoadAsync();

        Assert.True(loaded.Workspaces.Single(workspace => workspace.Id == dashboard.Id).Layout.ShowGridLines);
    }

    [Fact]
    public async Task LoadAsync_ADashboardSavedBeforeGridLinesExisted_DefaultsToThemBeingOff()
    {
        await File.WriteAllTextAsync(_configPath, """
            {"Workspaces":{"ActiveWorkspaceId":"w1","Workspaces":[
              {"Id":"w1","Name":"D","Type":"Dashboard","Layout":{"Columns":4,"Rows":4},"Panes":[]}]}}
            """);

        var loaded = await new WorkspaceSettingsStore(_configPath).LoadAsync();

        Assert.False(loaded.Workspaces[0].Layout.ShowGridLines, "a dashboard is something you look at, not a worksheet");
        Assert.Equal(4, loaded.Workspaces[0].Layout.Columns);
    }

    [Fact]
    public async Task SaveAsync_LeavesSiblingSectionsUntouched()
    {
        await File.WriteAllTextAsync(_configPath, """{"Layout":{"SidebarWidth":240},"Profiles":[]}""");

        await new WorkspaceSettingsStore(_configPath).SaveAsync(WorkspaceSettings.Default);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_configPath));
        Assert.Equal(240, document.RootElement.GetProperty("Layout").GetProperty("SidebarWidth").GetDouble());
    }

    [Fact]
    public async Task SaveAsync_ASessionsWorkspace_WritesNoDashboardGrid_SoTheFileHoldsNoSettingNothingReads()
    {
        await new WorkspaceSettingsStore(_configPath).SaveAsync(WorkspaceSettings.Default);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_configPath));
        var workspace = document.RootElement.GetProperty("Workspaces").GetProperty("Workspaces")[0];
        Assert.True(workspace.TryGetProperty("Layout", out var layout));
        Assert.Equal(JsonValueKind.Null, layout.ValueKind);
    }

    [Fact]
    public async Task LoadAsync_AWidgetPaneInASessionsWorkspace_DropsThePaneRatherThanRefusingToStart()
    {
        // A config that disagrees with itself is recoverable by ignoring the offending pane; refusing to load
        // would cost the operator the whole cockpit over one bad line.
        await File.WriteAllTextAsync(_configPath, """
            {"Workspaces":{"ActiveWorkspaceId":"w1","Workspaces":[
              {"Id":"w1","Name":"Work","Type":"Sessions","Panes":[
                {"Id":"p1","Kind":"Widget","WidgetId":"clock.time"},
                {"Id":"p2","Kind":"Terminal","Shell":"pwsh"}]}]}}
            """);

        var loaded = await new WorkspaceSettingsStore(_configPath).LoadAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(loaded.Workspaces));
        Assert.Equal(PaneKind.Terminal, Assert.Single(loaded.Workspaces.Single(workspace => workspace.Type == WorkspaceType.Sessions).Panes).Kind);
    }

    [Fact]
    public async Task LoadAsync_AnUnknownWorkspaceType_KeepsItsIdSoThePluginWorkspaceReturnsWhenItsPluginLoads()
    {
        // A type the host does not know is a plugin type whose plugin is not installed yet: it is kept, not
        // rewritten to a host type, so the workspace comes back intact once the plugin registers. Its grid panes
        // are dropped — a plugin workspace holds none — rather than the load throwing.
        await File.WriteAllTextAsync(_configPath, """
            {"Workspaces":{"ActiveWorkspaceId":"w1","Workspaces":[{"Id":"w1","Name":"?","Type":"autopilot.run","Panes":[{"Id":"p1","Kind":"AiSession"}]}]}}
            """);

        var loaded = await new WorkspaceSettingsStore(_configPath).LoadAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(loaded.Workspaces));
        var workspace = loaded.Workspaces.Single(workspace => workspace.Id == "w1");
        Assert.Equal(new WorkspaceType("autopilot.run"), workspace.Type);
        Assert.False(workspace.Type.IsBuiltIn);
        Assert.Empty(workspace.Panes);
    }

    [Fact]
    public async Task LoadAsync_ABlankWorkspaceType_FallsBackToSessionsRatherThanThrowing()
    {
        // A blank type is corruption, not a plugin id — recovered to the safe host default, the way an
        // unparseable enum used to be.
        await File.WriteAllTextAsync(_configPath, """
            {"Workspaces":{"ActiveWorkspaceId":"w1","Workspaces":[{"Id":"w1","Name":"?","Type":"","Panes":[]}]}}
            """);

        var loaded = await new WorkspaceSettingsStore(_configPath).LoadAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(loaded.Workspaces));
        Assert.Equal(WorkspaceType.Sessions, loaded.Workspaces.Single(workspace => workspace.Id == "w1").Type);
    }

    [Fact]
    public async Task LoadAsync_APluginWorkspaceType_RoundTripsThroughSaveAndLoad()
    {
        // The plugin type id must survive a save/load unchanged (it is an API surface), the way a widget id does.
        var store = new WorkspaceSettingsStore(_configPath);
        var settings = new WorkspaceSettings
        {
            Workspaces = [new Workspace("w1", "Autopilot", new WorkspaceType("autopilot.run"))],
            ActiveWorkspaceId = "w1",
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(loaded.Workspaces));
        Assert.Equal("autopilot.run", loaded.Workspaces.Single(workspace => workspace.Id == "w1").Type.Id);
    }

    [Fact]
    public async Task LoadAsync_AnEmptyWorkspaceList_YieldsTheDefaultRatherThanAnEmptyCockpit()
    {
        await File.WriteAllTextAsync(_configPath, """{"Workspaces":{"Workspaces":[]}}""");

        var loaded = await new WorkspaceSettingsStore(_configPath).LoadAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(loaded.Workspaces));
        Assert.NotNull(loaded.Active);
    }

    [Fact]
    public async Task LoadAsync_AnOutOfRangeDashboardGrid_IsClampedBeforeItReachesTheView()
    {
        await File.WriteAllTextAsync(_configPath, """
            {"Workspaces":{"ActiveWorkspaceId":"w1","Workspaces":[
              {"Id":"w1","Name":"D","Type":"Dashboard","Layout":{"Columns":0,"Rows":0},"Panes":[]}]}}
            """);

        var layout = (await new WorkspaceSettingsStore(_configPath).LoadAsync()).Workspaces[0].Layout;

        Assert.Equal(DashboardLayout.MinColumns, layout.Columns);
        Assert.Equal(DashboardLayout.MinRows, layout.Rows);
    }

    [Fact]
    public async Task LoadAsync_ADanglingActiveId_ResolvesToTheFirstWorkspace()
    {
        await File.WriteAllTextAsync(_configPath, """
            {"Workspaces":{"ActiveWorkspaceId":"gone","Workspaces":[{"Id":"w1","Name":"A","Type":"Sessions","Panes":[]}]}}
            """);

        Assert.Equal("w1", (await new WorkspaceSettingsStore(_configPath).LoadAsync()).ActiveWorkspaceId);
    }
}
