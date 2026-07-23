using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The MCP surface each CEO embed is scoped to (AC-197). Left on the request's default empty list a CEO inherits the
/// host's whole selection (161 tools observed) — every tool definition in its context. The scoped lists pin exactly the
/// endpoints each CEO needs, and are asserted here without a live embed so the minimal set does not drift.
/// </summary>
public class AutopilotCeoMcpScopeTests
{
    [Fact]
    public void PlanningCeo_CeoFirstRun_IsScopedToThePlanEndpointOnly()
    {
        // A CEO-first run has no source issue to keep in sync, so the planning CEO only emits the plan through
        // AutopilotPlanTools; nothing else is needed to plan.
        AutopilotPlanWorkspaceBody.PlanningCeoMcpServers(hasSource: false)
            .Should().ContainSingle().Which.Should().Be(AutopilotPlanTools.EndpointName);
        AutopilotPlanTools.EndpointName.Should().Be("cockpit-autopilot-plan");
    }

    [Fact]
    public void PlanningCeo_SourceTriggeredRun_IsAlsoScopedToThePlanEndpointOnly()
    {
        // AC-212: the planning CEO does NOT get the CEO (tracker) endpoint, even for a source-triggered run. That endpoint
        // is only mounted while a run is active (AutopilotPlugin gates it on manager.Active.Count > 0), so during planning
        // it mounts nothing — listing it only made the CEO grab for tracker tools it never had and report them missing.
        // Keeping the source issue in sync is the run's job (the CEO validator plus the coordinator's auto-advance, AC-202),
        // not the planning round's, so a source-triggered run plans on the plan endpoint alone, same as a CEO-first run.
        AutopilotPlanWorkspaceBody.PlanningCeoMcpServers(hasSource: true)
            .Should().ContainSingle().Which.Should().Be(AutopilotPlanTools.EndpointName);
    }

    [Fact]
    public void ValidatorCeo_IsScopedToTheCeoEndpoint_WhichHostsValidateAndTracker()
    {
        // The validator CEO calls autopilot_validate and the tracker-stage/note tools — all on the CEO endpoint. Mounting
        // it explicitly guarantees the tracker-stage flow works (the AC-197 uncertainty), rather than left to chance.
        AutopilotRunContext.ValidatorCeoMcpServers
            .Should().ContainSingle().Which.Should().Be(AutopilotCeoTools.EndpointName);
        AutopilotCeoTools.EndpointName.Should().Be("cockpit-autopilot-ceo");
    }
}
