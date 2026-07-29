using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// AC-410 step 1 against a real config file: a hand-written <c>cockpit.json</c> carrying an <c>AiSession</c> pane
/// with the four new fields (Title, NameIsChosen, SessionKind, ProjectId) loads with all of them intact, and an
/// unknown <c>SessionKind</c> string degrades to Sdk rather than refusing to load — same recovery posture as the
/// existing <c>WorkspaceSettingsStoreTests</c> for <c>Kind</c>/workspace <c>Type</c>.
/// </summary>
public class WorkspacePaneRestoreConfigTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"cockpit-workspaces-restore-{Guid.NewGuid():n}.json");

    public void Dispose()
    {
        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LoadAsync_AHandWrittenAiSessionPane_CarriesAllFourNewFields()
    {
        await File.WriteAllTextAsync(_configPath, """
            {"Workspaces":{"ActiveWorkspaceId":"w1","Workspaces":[
              {"Id":"w1","Name":"Work","Type":"Sessions","Panes":[
                {"Id":"p1","Kind":"AiSession","ProfileId":"work","WorkingDirectory":"/home/raymond/webshop",
                 "Title":"webshop","NameIsChosen":true,"SessionKind":"Tty","ProjectId":"proj-1"}]}]}}
            """);

        var loaded = await new WorkspaceSettingsStore(_configPath).LoadAsync();

        var pane = loaded.Workspaces.Single(workspace => workspace.Id == "w1").Panes.Single();
        Assert.Equal("webshop", pane.Title);
        Assert.True(pane.NameIsChosen);
        Assert.Equal(PaneSessionKind.Tty, pane.SessionKind);
        Assert.Equal("proj-1", pane.ProjectId);
        Assert.Equal("work", pane.ProfileId);
    }

    [Fact]
    public async Task LoadAsync_AnUnknownSessionKind_FallsBackToSdkRatherThanRefusingToLoad()
    {
        await File.WriteAllTextAsync(_configPath, """
            {"Workspaces":{"ActiveWorkspaceId":"w1","Workspaces":[
              {"Id":"w1","Name":"Work","Type":"Sessions","Panes":[
                {"Id":"p1","Kind":"AiSession","SessionKind":"some-future-kind"}]}]}}
            """);

        var loaded = await new WorkspaceSettingsStore(_configPath).LoadAsync();

        var pane = loaded.Workspaces.Single(workspace => workspace.Id == "w1").Panes.Single();
        Assert.Equal(PaneSessionKind.Sdk, pane.SessionKind);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAnAiSessionPaneThroughTheStore()
    {
        var store = new WorkspaceSettingsStore(_configPath);
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(new WorkspacePane("p1", PaneKind.AiSession)
        {
            ProfileId = "work",
            Title = "webshop",
            NameIsChosen = true,
            SessionKind = PaneSessionKind.Tty,
            ProjectId = "proj-1",
        });
        var saved = WorkspaceSettings.Default.WithWorkspace(sessions);

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        var pane = loaded.Workspaces.Single(workspace => workspace.Id == sessions.Id).Panes.Single();
        Assert.Equal("webshop", pane.Title);
        Assert.True(pane.NameIsChosen);
        Assert.Equal(PaneSessionKind.Tty, pane.SessionKind);
        Assert.Equal("proj-1", pane.ProjectId);
    }
}
