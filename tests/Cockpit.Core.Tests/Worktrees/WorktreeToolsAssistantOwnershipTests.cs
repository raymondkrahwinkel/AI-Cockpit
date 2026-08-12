using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Worktrees;
using NSubstitute;

namespace Cockpit.Core.Tests.Worktrees;

// AC-658: `WorktreeTools.RemoveAsync` reads liveness from the same `ILiveSessionRegistry` the AC-654 reconciler
// fix uses, so this drives the *real* `LiveSessionRegistry` — not a mock — against `WorktreeTools`, the way
// `WorktreeToolsTests` mocking `ILiveSessionRegistry` never could: a mock cannot show that the assistant is
// missing from the panes/sources the registry is actually wired with.
public class WorktreeToolsAssistantOwnershipTests
{
    [Fact]
    public async Task List_AssistantOwnedWorktree_ReportsOwnerLiveTrue_EvenWithNoPanesOrSourcesWired()
    {
        // AC-719 ronde B: worktree_list's `ownerLive` must read true for an assistant-owned worktree from the same
        // always-live construction WorktreeToolsAssistantOwnershipTests pins for RemoveAsync above — a real
        // registry, not a mock, is what actually proves the assistant is never absent from it.
        var manager = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord(AssistantIdentity.PaneId, "/repo", "/wt/assistant", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        manager.GetStatusesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorktreeStatus> { new(record, Exists: true, HasUncommittedChanges: false, StrandableCommits: 0) });
        var registry = new LiveSessionRegistry([]);
        var tools = new WorktreeTools(manager, registry);

        using var result = System.Text.Json.JsonDocument.Parse(await tools.ListAsync());

        var entry = result.RootElement.GetProperty("worktrees").EnumerateArray().Single();
        Assert.True(entry.GetProperty("ownerLive").GetBoolean());
    }

    [Fact]
    public async Task Remove_AssistantOwnedWorktree_RefusedByAnotherSession_EvenWithNoPanesOrSourcesWired()
    {
        var manager = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord(AssistantIdentity.PaneId, "/repo", "/wt/assistant", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });

        // The registry the assistant's own worktree would be checked against: no panes, no headless sources — the
        // assistant is never in either, by construction. If liveness relied on those alone this worktree would
        // read as ownerless and a different session's worktree_remove would be allowed to delete it out from
        // under the assistant actually working in it.
        var registry = new LiveSessionRegistry([]);
        var tools = new WorktreeTools(manager, registry);

        McpRequestContext.Set("some-other-session");
        try
        {
            var result = await tools.RemoveAsync("/wt/assistant");

            Assert.Contains("\"ok\":false", result);
            await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }
}
