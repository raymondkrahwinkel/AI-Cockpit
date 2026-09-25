using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1398: the backend holds an open workspace's runs and its planning CEO; the workspace only hands over its id.
public class AutopilotWorkspaceRunsTests
{
    [Fact]
    public void PlanningCeo_IsEmbeddedInItsWorkspace_BindsThePlanToItsPane_AndClosesOnce()
    {
        var request = new EmbeddedSessionRequest { ProfileId = "ceo" };
        var ceo = Substitute.For<IEmbeddedSession>();
        ceo.PaneId.Returns("ceo-pane");
        var host = Substitute.For<ICockpitHost>();
        host.EmbedSession("workspace-1", request).Returns(ceo);
        var plan = new AutopilotPlanController();

        var runs = _Runs(host, plan, _Manager());

        var embedded = runs.EmbedPlanningCeo(request);
        runs.ClosePlanningCeo();
        runs.ClosePlanningCeo();

        Assert.Equal((ceo, "ceo-pane"), (embedded, plan.SessionPaneId));
        ceo.Received(1).CloseAsync();
    }

    [Fact]
    public void Close_StopsBeingTheManagersRunner()
    {
        var manager = _Manager();
        var runs = _Runs(Substitute.For<ICockpitHost>(), new AutopilotPlanController(), manager);
        var attached = manager.Runner is not null;

        runs.Close();

        Assert.Equal((true, false), (attached, manager.Runner is not null));
    }

    private static AutopilotRunManager _Manager()
    {
        var storage = Substitute.For<IPluginStorage>();
        return new AutopilotRunManager(new AutopilotRunQueue(storage), new AutopilotSettings(storage));
    }

    private static AutopilotWorkspaceRuns _Runs(ICockpitHost host, AutopilotPlanController plan, AutopilotRunManager manager)
    {
        var storage = Substitute.For<IPluginStorage>();
        return new AutopilotWorkspaceRuns(
            host,
            "workspace-1",
            new AutopilotSettings(storage),
            plan,
            manager,
            new AutopilotRunQueue(storage),
            new AutopilotRunHistory(Substitute.For<IPluginCache>()),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            _ => Task.FromResult<string?>(null));
    }
}
