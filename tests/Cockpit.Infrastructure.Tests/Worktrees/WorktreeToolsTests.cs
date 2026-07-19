using System.Text.Json;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Worktrees;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Worktrees;

/// <summary>The agent-facing worktree MCP tools (AC-104): thin over <see cref="IWorktreeManager"/>, returning the path/branch on create and refusing a remove of a path it does not manage.</summary>
public class WorktreeToolsTests
{
    [Fact]
    public async Task Create_ReturnsThePathAndBranch()
    {
        var manager = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord("pane", "/repo", "/wt/path", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        manager.CreateForSessionAsync("pane", null, "/repo", Arg.Any<CancellationToken>()).Returns(record);
        var tools = new WorktreeTools(manager);

        using var result = JsonDocument.Parse(await tools.CreateAsync("pane", "/repo"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("path").GetString().Should().Be("/wt/path");
        result.RootElement.GetProperty("branch").GetString().Should().Be("cockpit/x");
    }

    [Fact]
    public async Task Remove_PathNotManaged_ReturnsNotOkAndRemovesNothing()
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord>());
        var tools = new WorktreeTools(manager);

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/nope"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_OwnerSessionStillLive_RefusesAndRemovesNothing()
    {
        var manager = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord("live-pane", "/repo", "/wt/live", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var live = Substitute.For<ILiveSessionRegistry>();
        live.LiveSessionIds.Returns(new HashSet<string>(StringComparer.Ordinal) { "live-pane" });
        var tools = new WorktreeTools(manager, live);

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/live", force: true));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_OwnerSessionGone_RemovesTheWorktree()
    {
        var manager = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord("gone-pane", "/repo", "/wt/gone", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var live = Substitute.For<ILiveSessionRegistry>();
        live.LiveSessionIds.Returns(new HashSet<string>(StringComparer.Ordinal));
        var tools = new WorktreeTools(manager, live);

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/gone"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        await manager.Received(1).RemoveAsync(record, false, Arg.Any<CancellationToken>());
    }
}
