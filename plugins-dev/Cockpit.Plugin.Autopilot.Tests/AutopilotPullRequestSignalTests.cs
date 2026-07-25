using Cockpit.Plugins.Abstractions;
using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The PR-delivery signal path (AC-216): a plugin template's <see cref="PluginAutopilotTemplate.DeliversPullRequest"/>
/// rides through <see cref="AutopilotTemplate.ForPlugin"/> and is stamped on the approved <see cref="AutopilotPlan"/>, so
/// the run's finalizer knows a code run must end with a PR while an admin run does not. Also pins the autopilot-own tools
/// pre-authorized for a run's sessions (AC-215).
/// </summary>
public class AutopilotPullRequestSignalTests
{
    [Fact]
    public void ForPlugin_CarriesTheDeliversPullRequestSignal()
    {
        var code = AutopilotTemplate.ForPlugin("youtrack", new PluginAutopilotTemplate("t.code", "Bug fix", "body", null, DeliversPullRequest: true));
        var admin = AutopilotTemplate.ForPlugin("youtrack", new PluginAutopilotTemplate("t.admin", "Triage", "body"));

        code.DeliversPullRequest.Should().BeTrue();
        admin.DeliversPullRequest.Should().BeFalse();
    }

    [Fact]
    public void Plan_DefaultsToNoPr_AndCanBeStampedAtApproval()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Do the thing");
        plan.DeliversPullRequest.Should().BeFalse();

        plan.WithDeliversPullRequest(true).DeliversPullRequest.Should().BeTrue();
        // The stamp is a pure with-copy — the original is untouched.
        plan.DeliversPullRequest.Should().BeFalse();
    }

    [Fact]
    public void PreApprovedRunTools_AreOnlyAutopilotsOwnControlTools_NeverFileOrShell()
    {
        AutopilotRunToolNames.ForStepWorker.Should().Contain("mcp__cockpit-autopilot-run__autopilot_step_done");
        AutopilotRunToolNames.ForStepWorker.Should().Contain("mcp__cockpit-autopilot-run__autopilot_blocked");
        AutopilotRunToolNames.ForValidatorCeo.Should().Contain("mcp__cockpit-autopilot-ceo__autopilot_validate");

        // Every pre-approved name is an autopilot endpoint tool — nothing else is pre-authorized.
        AutopilotRunToolNames.ForStepWorker.Concat(AutopilotRunToolNames.ForValidatorCeo)
            .Should().OnlyContain(tool => tool.StartsWith("mcp__cockpit-autopilot-"));
    }
}
