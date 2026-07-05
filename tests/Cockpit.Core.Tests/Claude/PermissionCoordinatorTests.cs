using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Infrastructure.Claude.Permissions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Exercises the tool_use_id correlation and allow/deny resolution in
/// <see cref="PermissionCoordinator"/> — the bridge between the MCP tool and the sessions.
/// </summary>
public class PermissionCoordinatorTests
{
    [Fact]
    public async Task RequestDecisionAsync_CompletesWithAllow_WhenResolvedAllow()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);
        var pending = coordinator.RequestDecisionAsync("toolu_1", "Write", """{"file_path":"a.txt"}""");

        var resolved = coordinator.Resolve("toolu_1", PermissionDecision.Allow());

        resolved.Should().BeTrue();
        var decision = await pending;
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task RequestDecisionAsync_CompletesWithDeny_WhenResolvedDeny()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);
        var pending = coordinator.RequestDecisionAsync("toolu_2", "Write", "{}");

        coordinator.Resolve("toolu_2", PermissionDecision.Deny("nope"));

        var decision = await pending;
        decision.IsAllowed.Should().BeFalse();
        decision.DenyMessage.Should().Be("nope");
    }

    [Fact]
    public void Resolve_WithNoPendingRequest_ReturnsFalse()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);

        coordinator.Resolve("unknown", PermissionDecision.Allow()).Should().BeFalse();
    }

    [Fact]
    public async Task DecisionsAreCorrelatedPerToolUseId()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);
        var first = coordinator.RequestDecisionAsync("toolu_a", "Write", "{}");
        var second = coordinator.RequestDecisionAsync("toolu_b", "Write", "{}");

        coordinator.Resolve("toolu_b", PermissionDecision.Deny("b denied"));
        coordinator.Resolve("toolu_a", PermissionDecision.Allow());

        (await first).IsAllowed.Should().BeTrue();
        var secondDecision = await second;
        secondDecision.IsAllowed.Should().BeFalse();
        secondDecision.DenyMessage.Should().Be("b denied");
    }

    [Fact]
    public async Task DenyPending_DeniesTheListedRequests()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);
        var pending = coordinator.RequestDecisionAsync("toolu_c", "Write", "{}");

        coordinator.DenyPending(["toolu_c"], "session closed");

        var decision = await pending;
        decision.IsAllowed.Should().BeFalse();
        decision.DenyMessage.Should().Be("session closed");
    }

    [Fact]
    public async Task RequestDecisionAsync_WhenCancelled_CompletesAsDeny()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);
        using var cts = new CancellationTokenSource();
        var pending = coordinator.RequestDecisionAsync("toolu_d", "Write", "{}", cts.Token);

        await cts.CancelAsync();

        var decision = await pending;
        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Resolve_AfterCompletion_ReturnsFalse()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);
        var pending = coordinator.RequestDecisionAsync("toolu_e", "Write", "{}");
        coordinator.Resolve("toolu_e", PermissionDecision.Allow());
        await pending;

        coordinator.Resolve("toolu_e", PermissionDecision.Deny("late")).Should().BeFalse();
    }

    [Fact]
    public async Task RequestDecisionAsync_WithAMatchingRule_ShortCircuitsToAllowWithoutPrompting()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);
        var rules = new PermissionRuleSet([PermissionRule.ForWildcard("Bash")]);
        coordinator.RegisterToolUse("toolu_f", rules);

        // No Resolve is ever called: a matching rule must complete the request on its own.
        var decision = await coordinator.RequestDecisionAsync("toolu_f", "Bash", """{"command":"ls"}""");

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task RequestDecisionAsync_WithANonMatchingRule_StillPromptsAndAwaitsResolve()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);
        var rules = new PermissionRuleSet([PermissionRule.ForWildcard("Bash")]);
        coordinator.RegisterToolUse("toolu_g", rules);

        var pending = coordinator.RequestDecisionAsync("toolu_g", "Edit", "{}");

        pending.IsCompleted.Should().BeFalse("Edit is not covered by the Bash rule, so it must wait for the operator");
        coordinator.Resolve("toolu_g", PermissionDecision.Deny("no")).Should().BeTrue();
        (await pending).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task RegisterToolUse_WithNullChecker_PromptsAsNormal()
    {
        var coordinator = new PermissionCoordinator(NullLogger<PermissionCoordinator>.Instance);
        coordinator.RegisterToolUse("toolu_h", ruleChecker: null);

        var pending = coordinator.RequestDecisionAsync("toolu_h", "Bash", "{}");

        pending.IsCompleted.Should().BeFalse();
        coordinator.Resolve("toolu_h", PermissionDecision.Allow());
        (await pending).IsAllowed.Should().BeTrue();
    }
}
