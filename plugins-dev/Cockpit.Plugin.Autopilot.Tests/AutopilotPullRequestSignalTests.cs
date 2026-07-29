using Cockpit.Plugins.Abstractions;

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

        Assert.True(code.DeliversPullRequest);
        Assert.False(admin.DeliversPullRequest);
    }

    [Fact]
    public void Plan_DefaultsToNoPr_AndCanBeStampedAtApproval()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Do the thing");
        Assert.False(plan.DeliversPullRequest);

        Assert.True(plan.WithDeliversPullRequest(true).DeliversPullRequest);
        // The stamp is a pure with-copy — the original is untouched.
        Assert.False(plan.DeliversPullRequest);
    }

    [Fact]
    public void PreApprovedRunTools_AreOnlyAutopilotsOwnControlTools_NeverFileOrShell()
    {
        Assert.Contains("mcp__cockpit-autopilot-run__autopilot_step_done", AutopilotRunToolNames.ForStepWorker);
        Assert.Contains("mcp__cockpit-autopilot-run__autopilot_blocked", AutopilotRunToolNames.ForStepWorker);
        Assert.Contains("mcp__cockpit-autopilot-ceo__autopilot_validate", AutopilotRunToolNames.ForValidatorCeo);

        // Every pre-approved name is an autopilot endpoint tool — nothing else is pre-authorized.
        Assert.All(
            AutopilotRunToolNames.ForStepWorker.Concat(AutopilotRunToolNames.ForValidatorCeo),
            tool => Assert.StartsWith("mcp__cockpit-autopilot-", tool));
    }
}
