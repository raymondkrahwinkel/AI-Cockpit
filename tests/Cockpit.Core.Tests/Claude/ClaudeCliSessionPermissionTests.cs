using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Infrastructure.Claude;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Exercises how <see cref="ClaudeCliSession"/> feeds operator decisions back to the CLI via the
/// <see cref="RecordingPermissionCoordinator"/>: allow/deny resolution and deny-on-dispose so a
/// closing session never leaves a tool call blocked.
/// </summary>
public class ClaudeCliSessionPermissionTests
{
    [Fact]
    public async Task RespondToPermissionAsync_Allow_ResolvesTheCoordinatorWithAllow()
    {
        var coordinator = new RecordingPermissionCoordinator();
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, coordinator, NullLogger<ClaudeCliSession>.Instance);
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
        await using var session = new ClaudeCliSession(process, coordinator, NullLogger<ClaudeCliSession>.Instance);
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
        var session = new ClaudeCliSession(process, coordinator, NullLogger<ClaudeCliSession>.Instance);
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
        var session = new ClaudeCliSession(process, coordinator, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();
        await DrainEventsAsync(session);
        await session.RespondToPermissionAsync("toolu_ans", allow: true);

        await session.DisposeAsync();

        coordinator.Denied.Should().NotContain(d => d.ToolUseId == "toolu_ans");
    }

    private static async Task DrainEventsAsync(ClaudeCliSession session)
    {
        await foreach (var _ in session.Events)
        {
        }
    }
}
