using System.ComponentModel;
using System.Reflection;
using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The step agent's MCP tools (AC-193, AC-201): the wording of the <c>autopilot_blocked</c> tool description, which the
/// model reads to decide when to stop. It is asserted here rather than through a live session so the reframing — prefer
/// a documented assumption, and when you cannot, consult your MANAGER (the CEO), not the operator directly — is pinned
/// without a running run.
/// </summary>
public class AutopilotRunToolsTests
{
    private static string BlockedDescription()
    {
        var method = typeof(AutopilotRunTools).GetMethod(nameof(AutopilotRunTools.Blocked), BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull();
        return method!.GetCustomAttribute<DescriptionAttribute>()!.Description;
    }

    [Fact]
    public void BlockedDescription_PrefersAnAssumption_AndNoLongerTellsTheAgentToUseItInsteadOfGuessing()
    {
        var description = BlockedDescription();

        // AC-193: the old "Use this instead of guessing" steered the agent to block on any uncertainty. It is gone.
        description.Should().NotContain("instead of guessing");
        // It still prefers a documented assumption + carrying on before escalating.
        description.Should().Contain("documented, reasonable assumption");
    }

    [Fact]
    public void BlockedDescription_FramesItAsConsultingTheManager_NotReachingTheOperatorDirectly()
    {
        var description = BlockedDescription();

        // AC-201: autopilot_blocked now routes to the worker's manager (the CEO), which answers or escalates — the tool
        // must say so, and say it does NOT go straight to the operator.
        description.Should().Contain("manager");
        description.Should().Contain("does NOT go straight to the operator");
        description.Should().Contain("escalates");
    }
}
