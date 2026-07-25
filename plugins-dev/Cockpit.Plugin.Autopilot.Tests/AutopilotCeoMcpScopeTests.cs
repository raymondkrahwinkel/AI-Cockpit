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
        // A CEO-first run has no source issue, so no tracker read servers — the planning CEO only emits the plan through
        // AutopilotPlanTools; nothing else is needed to plan.
        AutopilotPlanWorkspaceBody.PlanningCeoMcpServers(trackerReadServers: null)
            .Should().ContainSingle().Which.Should().Be(AutopilotPlanTools.EndpointName);
        AutopilotPlanWorkspaceBody.PlanningCeoMcpServers(trackerReadServers: [])
            .Should().ContainSingle().Which.Should().Be(AutopilotPlanTools.EndpointName);
        AutopilotPlanTools.EndpointName.Should().Be("cockpit-autopilot-plan");
    }

    [Fact]
    public void PlanningCeo_SourceTriggeredRun_AddsTheTrackerReadServers_ButNotTheWriteEndpoint()
    {
        // AC-212 read/write split: a source-triggered run scopes the planning CEO to the plan endpoint plus the tracker's
        // READ-only MCP servers (so it can read the issue and, for an epic, pull its children — AC-217). The CEO (write)
        // endpoint that hosts autopilot_tracker_stage / autopilot_tracker_note is NEVER in the planning scope: it is only
        // mounted while a run is active, and moving the issue before approval would be premature — stage/notes stay the
        // run's job (the CEO validator plus the coordinator's auto-advance, AC-202).
        var scope = AutopilotPlanWorkspaceBody.PlanningCeoMcpServers(trackerReadServers: ["YouTrack: Personal"]);

        scope.Should().BeEquivalentTo(new[] { AutopilotPlanTools.EndpointName, "YouTrack: Personal" });
        scope.Should().NotContain(AutopilotCeoTools.EndpointName);
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
