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
        // AC-410: an AiSession pane comes back with what restoring it needs.
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(new WorkspacePane("p2", PaneKind.AiSession)
        {
            ProfileId = "work",
            Title = "webshop",
            NameIsChosen = true,
            SessionKind = PaneSessionKind.Tty,
            ProjectId = "proj-1",
        });
        var saved = WorkspaceSettings.Default.WithWorkspace(sessions).WithWorkspace(dashboard);

        // AC-1306: and the sidebar tree's folded nodes, or every start begins at the bottom of your own tree.
        var folded = saved.Workspaces.First(workspace => workspace.Type == WorkspaceType.Sessions).Id;
        saved = saved.WithSidebarCollapsed(folded, collapsed: true);

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        Assert.Equal(4, System.Linq.Enumerable.Count(loaded.Workspaces));
        Assert.Equal(dashboard.Id, loaded.ActiveWorkspaceId);
        Assert.Equal([folded], loaded.CollapsedSidebarWorkspaceIds);
        var reloaded = loaded.Workspaces.Single(workspace => workspace.Id == dashboard.Id);
        Assert.Equal("Monitoring", reloaded.Name);
        Assert.Equal(3, reloaded.Layout.Columns);
        Assert.Equivalent(dashboard.Panes[0], Assert.Single(reloaded.Panes));

        var pane = loaded.Workspaces.Single(workspace => workspace.Id == sessions.Id).Panes.Single();
        Assert.Equal("webshop", pane.Title);
        Assert.True(pane.NameIsChosen);
        Assert.Equal(PaneSessionKind.Tty, pane.SessionKind);
        Assert.Equal("proj-1", pane.ProjectId);
    }

    [Fact]
    public async Task LoadAsync_AnUnknownWorkspaceType_KeepsItsIdSoThePluginWorkspaceReturnsWhenItsPluginLoads()
    {
        // An unknown type is a plugin type whose plugin is not installed yet: kept, not rewritten, so the workspace
        // returns once the plugin registers; its grid panes are dropped rather than the load throwing. Likewise an
        // unknown pane SessionKind (AC-410) degrades to Sdk.
        await File.WriteAllTextAsync(_configPath, """
            {"Workspaces":{"ActiveWorkspaceId":"w1","Workspaces":[{"Id":"w1","Name":"?","Type":"autopilot.run","Panes":[{"Id":"p1","Kind":"AiSession"}]},
              {"Id":"w2","Name":"Work","Type":"Sessions","Panes":[{"Id":"p2","Kind":"AiSession","SessionKind":"some-future-kind"}]}]}}
            """);

        var loaded = await new WorkspaceSettingsStore(_configPath).LoadAsync();

        var workspace = loaded.Workspaces.Single(workspace => workspace.Id == "w1");
        Assert.Equal(new WorkspaceType("autopilot.run"), workspace.Type);
        Assert.False(workspace.Type.IsBuiltIn);
        Assert.Empty(workspace.Panes);
        Assert.Equal(PaneSessionKind.Sdk, loaded.Workspaces.Single(workspace => workspace.Id == "w2").Panes.Single().SessionKind);
    }
}
