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
    public void PlanningCeo_SourceTriggeredRun_AlsoGetsTheCeoEndpointForTracker()
    {
        // A source-triggered run's brief tells the planning CEO to move the issue's stage and leave notes via the tracker
        // tools on the CEO endpoint — so that endpoint must be mounted too, or the brief names tools the session lacks.
        AutopilotPlanWorkspaceBody.PlanningCeoMcpServers(hasSource: true)
            .Should().BeEquivalentTo(new[] { AutopilotPlanTools.EndpointName, AutopilotCeoTools.EndpointName });
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
