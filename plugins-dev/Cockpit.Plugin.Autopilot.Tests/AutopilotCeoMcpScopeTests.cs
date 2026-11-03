
namespace Cockpit.Plugin.Autopilot.Tests;

// The MCP surface each CEO embed is scoped to (AC-197). Left on the request's default empty list a CEO inherits the
// host's whole selection (161 tools observed) — every tool definition in its context. The scoped lists pin exactly the
// endpoints each CEO needs, and are asserted here without a live embed so the minimal set does not drift.
public class AutopilotCeoMcpScopeTests
{
    [Fact]
    public void EachCeo_IsScopedToItsOwnEndpoints_AndThePlanningCeoNeverGetsTheWriteEndpoint()
    {
        // A CEO-first run has no source issue, so no tracker read servers — the planning CEO only emits the plan.
        Assert.Equal(AutopilotPlanTools.EndpointName, Assert.Single(AutopilotWorkspaceRuns.PlanningCeoMcpServers(trackerReadServers: null)));
        Assert.Equal(AutopilotPlanTools.EndpointName, Assert.Single(AutopilotWorkspaceRuns.PlanningCeoMcpServers(trackerReadServers: [])));

        // AC-212 read/write split: a source-triggered run adds the tracker's READ-only servers. The write endpoint is
        // NEVER in the planning scope — moving the issue before approval would be premature (AC-202).
        var scope = AutopilotWorkspaceRuns.PlanningCeoMcpServers(trackerReadServers: ["YouTrack: Personal"]);
        Assert.Equivalent(new[] { AutopilotPlanTools.EndpointName, "YouTrack: Personal" }, scope);
        Assert.DoesNotContain(AutopilotCeoTools.EndpointName, scope);

        // The validator CEO calls autopilot_validate and the tracker-stage/note tools — all on the CEO endpoint.
        Assert.Equal(AutopilotCeoTools.EndpointName, Assert.Single(AutopilotRunContext.ValidatorCeoMcpServers));
    }
}
