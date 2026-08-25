
namespace Cockpit.Plugin.Autopilot.Tests;

// The MCP surface each CEO embed is scoped to (AC-197). Left on the request's default empty list a CEO inherits the
// host's whole selection (161 tools observed) — every tool definition in its context. The scoped lists pin exactly the
// endpoints each CEO needs, and are asserted here without a live embed so the minimal set does not drift.
public class AutopilotCeoMcpScopeTests
{
    [Fact]
    public void PlanningCeo_CeoFirstRun_IsScopedToThePlanEndpointOnly()
    {
        // A CEO-first run has no source issue, so no tracker read servers — the planning CEO only emits the plan through
        // AutopilotPlanTools; nothing else is needed to plan.
        Assert.Equal(AutopilotPlanTools.EndpointName, Assert.Single(AutopilotPlanWorkspaceBody.PlanningCeoMcpServers(trackerReadServers: null)));
        Assert.Equal(AutopilotPlanTools.EndpointName, Assert.Single(AutopilotPlanWorkspaceBody.PlanningCeoMcpServers(trackerReadServers: [])));
        Assert.Equal("cockpit-autopilot-plan", AutopilotPlanTools.EndpointName);
    }

    [Fact]
    public void PlanningCeo_SourceTriggeredRun_AddsTheTrackerReadServers_ButNotTheWriteEndpoint()
    {
        // AC-212 read/write split: a source-triggered run scopes the planning CEO to the plan endpoint plus the
        // tracker's READ-only MCP servers. The write endpoint is NEVER in the planning scope — it mounts only while
        // a run is active, since moving the issue before approval would be premature (stage/notes are the run's job, AC-202).
        var scope = AutopilotPlanWorkspaceBody.PlanningCeoMcpServers(trackerReadServers: ["YouTrack: Personal"]);

        Assert.Equivalent(new[] { AutopilotPlanTools.EndpointName, "YouTrack: Personal" }, scope);
        Assert.DoesNotContain(AutopilotCeoTools.EndpointName, scope);
    }

    [Fact]
    public void ValidatorCeo_IsScopedToTheCeoEndpoint_WhichHostsValidateAndTracker()
    {
        // The validator CEO calls autopilot_validate and the tracker-stage/note tools — all on the CEO endpoint. Mounting
        // it explicitly guarantees the tracker-stage flow works (the AC-197 uncertainty), rather than left to chance.
        Assert.Equal(AutopilotCeoTools.EndpointName, Assert.Single(AutopilotRunContext.ValidatorCeoMcpServers));
        Assert.Equal("cockpit-autopilot-ceo", AutopilotCeoTools.EndpointName);
    }
}
