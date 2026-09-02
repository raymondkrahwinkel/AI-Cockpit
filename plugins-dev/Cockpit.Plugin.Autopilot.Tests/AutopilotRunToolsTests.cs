using System.ComponentModel;
using System.Reflection;

namespace Cockpit.Plugin.Autopilot.Tests;

// The step agent's MCP tools (AC-193, AC-201): the wording of `autopilot_blocked`'s description, which the model
// reads to decide when to stop. Asserted here rather than through a live session so the reframing — prefer a
// documented assumption, else consult your MANAGER (the CEO), not the operator — is pinned without a running run.
public class AutopilotRunToolsTests
{
    public static IEnumerable<object[]> BlockedDescriptionClaims() =>
    [
        // AC-193: the old "Use this instead of guessing" steered the agent to block on any uncertainty. It is gone,
        // and what replaced it is a documented assumption plus carrying on before escalating.
        [new[] { "documented, reasonable assumption" }, new[] { "instead of guessing" }],
        // AC-201: autopilot_blocked routes to the worker's manager (the CEO), which answers or escalates — the tool
        // must say so, and say it does NOT go straight to the operator.
        [new[] { "manager", "does NOT go straight to the operator", "escalates" }, Array.Empty<string>()],
    ];

    [Theory]
    [MemberData(nameof(BlockedDescriptionClaims))]
    public void BlockedDescription_SendsTheAgentToItsManager_NotToTheOperator_AndNotStraightToABlock(string[] present, string[] absent)
    {
        var method = typeof(AutopilotRunTools).GetMethod(nameof(AutopilotRunTools.Blocked), BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        var description = method!.GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.All(present, fragment => Assert.Contains(fragment, description));
        Assert.All(absent, fragment => Assert.DoesNotContain(fragment, description));
    }
}
