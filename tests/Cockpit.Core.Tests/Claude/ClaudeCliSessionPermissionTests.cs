using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Claude;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Exercises how <see cref="ClaudeCliSession"/> feeds operator decisions back to the CLI via the
/// <see cref="RecordingPermissionCoordinator"/>: allow/deny resolution, deny-on-dispose so a
/// closing session never leaves a tool call blocked, and the always-allow rule flow (register the
/// profile's rules per tool_use, persist an "always" choice, and load saved rules on start).
/// </summary>
public class ClaudeCliSessionPermissionTests
{
    [Fact]
    public async Task RespondToPermissionAsync_Allow_ResolvesTheCoordinatorWithAllow()
    {
        var coordinator = new RecordingPermissionCoordinator();
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, coordinator, new InMemoryPermissionRuleStore(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.RespondToPermissionAsync("toolu_1", allow: true);

        coordinator.Resolved.Should().ContainSingle();
        coordinator.Resolved[0].ToolUseId.Should().Be("toolu_1");
        coordinator.Resolved[0].Decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task RespondToPermissionAsync_Deny_ResolvesTheCoordinatorWithDenyAndMessage()
    {
        var coordinator = new RecordingPermissionCoordinator();
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, coordinator, new InMemoryPermissionRuleStore(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.RespondToPermissionAsync("toolu_2", allow: false);

        coordinator.Resolved.Should().ContainSingle();
        coordinator.Resolved[0].Decision.IsAllowed.Should().BeFalse();
        coordinator.Resolved[0].Decision.DenyMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Dispose_DeniesToolUseIdsThatWereSeenButNeverAnswered()
    {
        var coordinator = new RecordingPermissionCoordinator();
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"assistant","session_id":"S1","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_open","name":"Write","input":{"file_path":"a.txt"}}]}}""");
        process.CompleteOutput();
        var session = new ClaudeCliSession(process, coordinator, new InMemoryPermissionRuleStore(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();
        await DrainEventsAsync(session);

        await session.DisposeAsync();

        coordinator.Denied.Should().Contain(d => d.ToolUseId == "toolu_open");
    }

    [Fact]
    public async Task Dispose_DoesNotDenyAToolUseIdThatWasAlreadyAnswered()
    {
        var coordinator = new RecordingPermissionCoordinator();
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"assistant","session_id":"S1","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_ans","name":"Write","input":{"file_path":"a.txt"}}]}}""");
        process.CompleteOutput();
        var session = new ClaudeCliSession(process, coordinator, new InMemoryPermissionRuleStore(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();
        await DrainEventsAsync(session);
        await session.RespondToPermissionAsync("toolu_ans", allow: true);

        await session.DisposeAsync();

        coordinator.Denied.Should().NotContain(d => d.ToolUseId == "toolu_ans");
    }

    [Fact]
    public async Task SeeingAToolUse_RegistersTheProfilesRulesWithTheCoordinator()
    {
        var coordinator = new RecordingPermissionCoordinator();
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"assistant","session_id":"S1","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_reg","name":"Bash","input":{"command":"ls"}}]}}""");
        process.CompleteOutput();
        var store = new InMemoryPermissionRuleStore("work", PermissionRule.ForWildcard("Bash"));
        var session = new ClaudeCliSession(process, coordinator, store, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync(new ClaudeProfile("work", @"C:\fake"));
        await DrainEventsAsync(session);

        var registration = coordinator.Registered.Should().ContainSingle(r => r.ToolUseId == "toolu_reg").Subject;
        registration.RuleChecker.Should().NotBeNull();
        registration.RuleChecker!.IsAlwaysAllowed("Bash", """{"command":"ls"}""").Should().BeTrue();

        await session.DisposeAsync();
    }

    [Fact]
    public async Task AllowPermissionAlwaysAsync_Wildcard_PersistsTheRuleAndResolvesAllow()
    {
        var coordinator = new RecordingPermissionCoordinator();
        var process = new FakeClaudeCliProcess();
        var store = new InMemoryPermissionRuleStore();
        await using var session = new ClaudeCliSession(process, coordinator, store, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync(new ClaudeProfile("work", @"C:\fake"));

        await session.AllowPermissionAlwaysAsync("toolu_x", "Bash", """{"command":"ls"}""", PermissionRuleScope.Wildcard);

        store.RulesFor("work").Should().ContainSingle().Which.Should().Be(PermissionRule.ForWildcard("Bash"));
        coordinator.Resolved.Should().ContainSingle(r => r.ToolUseId == "toolu_x" && r.Decision.IsAllowed);
    }

    [Fact]
    public async Task AllowPermissionAlwaysAsync_Exact_PersistsAnExactRuleKeyedOnTheInput()
    {
        var coordinator = new RecordingPermissionCoordinator();
        var process = new FakeClaudeCliProcess();
        var store = new InMemoryPermissionRuleStore();
        await using var session = new ClaudeCliSession(process, coordinator, store, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync(new ClaudeProfile("work", @"C:\fake"));

        await session.AllowPermissionAlwaysAsync("toolu_y", "Bash", """{"command":"dotnet build"}""", PermissionRuleScope.Exact);

        var rule = store.RulesFor("work").Should().ContainSingle().Subject;
        rule.Scope.Should().Be(PermissionRuleScope.Exact);
        rule.Matches("Bash", """{"command":"dotnet build"}""").Should().BeTrue();
        rule.Matches("Bash", """{"command":"dotnet test"}""").Should().BeFalse();
    }

    [Fact]
    public async Task AllowPermissionAlwaysAsync_MakesTheLiveSetShortCircuitTheNextMatchingCall()
    {
        var coordinator = new RecordingPermissionCoordinator();
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"assistant","session_id":"S1","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_next","name":"Bash","input":{"command":"ls"}}]}}""");
        process.CompleteOutput();
        await using var session = new ClaudeCliSession(process, coordinator, new InMemoryPermissionRuleStore(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync(new ClaudeProfile("work", @"C:\fake"));

        await session.AllowPermissionAlwaysAsync("toolu_first", "Bash", """{"command":"ls"}""", PermissionRuleScope.Wildcard);
        await DrainEventsAsync(session);

        // The rule set the session registered for the following tool_use must now cover it.
        var registration = coordinator.Registered.Should().ContainSingle(r => r.ToolUseId == "toolu_next").Subject;
        registration.RuleChecker!.IsAlwaysAllowed("Bash", """{"command":"anything"}""").Should().BeTrue();
    }

    private static async Task DrainEventsAsync(ClaudeCliSession session)
    {
        await foreach (var _ in session.Events)
        {
        }
    }
}
