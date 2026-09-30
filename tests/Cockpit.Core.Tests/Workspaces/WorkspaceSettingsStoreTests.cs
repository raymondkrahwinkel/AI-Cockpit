using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

// The recovery cases matter more than the happy path: a malformed workspaces section must not cost the operator their cockpit.
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

        // AC-1306: and the sidebar tree's folded nodes, or every start begins at the bottom of your own tree.
        var folded = saved.Workspaces.First(workspace => workspace.Type == WorkspaceType.Sessions).Id;
        saved = saved.WithSidebarCollapsed(folded, collapsed: true);

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        Assert.Equal(3, System.Linq.Enumerable.Count(loaded.Workspaces));
        Assert.Equal(dashboard.Id, loaded.ActiveWorkspaceId);
        Assert.Equal([folded], loaded.CollapsedSidebarWorkspaceIds);
        var reloaded = loaded.Workspaces.Single(workspace => workspace.Id == dashboard.Id);
        Assert.Equal("Monitoring", reloaded.Name);
        Assert.Equal(3, reloaded.Layout.Columns);
        Assert.Equivalent(dashboard.Panes[0], Assert.Single(reloaded.Panes));
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

}
