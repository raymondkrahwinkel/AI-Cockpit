using System.Text.Json;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Worktrees;

/// <summary>
/// The agent-facing worktree MCP tools (AC-104): thin over <see cref="IWorktreeManager"/>, returning the path/branch
/// on create, refusing a remove of a path it does not manage or a live session's tree, and gating a dirty removal
/// behind operator consent.
/// </summary>
public class WorktreeToolsTests
{

    [Fact]
    public async Task Remove_RefusesAWorktreeOwnedByAnotherSession_KeyedOnTheVerifiedPane()
    {
        // AC-128: an agent may only remove a worktree it owns. Naming another session's path is a confused deputy.
        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record("victim-pane", "/wt/victim");
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var tools = new WorktreeTools(manager);

        McpRequestContext.Set("attacker-pane");
        try
        {
            using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/victim"));

            Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("another session", result.RootElement.GetProperty("error").GetString());
            await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    private static WorktreeRecord _Record(string session, string path) =>
        new(session, "/repo", path, "cockpit/x", "abc", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Remove_OwnerSessionIsDeadAndLivenessIsKnown_AllowsTheCrossSessionRemoval()
    {
        // AC-524's third defect: guard 1 used to be categorical regardless of liveness, so a worktree an agent made
        // in a session that has since crashed could never be cleaned up by any other agent — only a bare
        // `git worktree remove --force --force` from outside Cockpit could reclaim it. Once the owner is provably
        // dead (absent from LiveSessionIds), there is no live session left to protect from a confused deputy.
        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record("crashed-pane", "/wt/orphan");
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        manager.HasUncommittedChangesAsync(record, Arg.Any<CancellationToken>()).Returns(false);
        var live = Substitute.For<ILiveSessionRegistry>();
        live.LiveSessionIds.Returns(new HashSet<string>(StringComparer.Ordinal));
        var tools = new WorktreeTools(manager, live);

        McpRequestContext.Set("some-other-live-pane");
        try
        {
            using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/orphan"));

            Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
            await manager.Received(1).RemoveAsync(record, false, Arg.Any<CancellationToken>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task Remove_AgentCreatedWorktree_OwnerSessionStillLive_AllowsSelfCleanup()
    {
        // AC-520 fix 5: an agent-made worktree (worktree_create) has nobody running "in" it — refusing to remove it
        // just because its own session happens to still be live left the tool unusable for exactly the case its own
        // description promises ("when a task is done").
        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record("my-pane", "/wt/agent-made") with { IsAgentCreated = true };
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        manager.HasUncommittedChangesAsync(record, Arg.Any<CancellationToken>()).Returns(false);
        var live = Substitute.For<ILiveSessionRegistry>();
        live.LiveSessionIds.Returns(new HashSet<string>(StringComparer.Ordinal) { "my-pane" });
        var tools = new WorktreeTools(manager, live);

        McpRequestContext.Set("my-pane");
        try
        {
            using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/agent-made"));

            Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
            await manager.Received(1).RemoveAsync(record, false, Arg.Any<CancellationToken>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task Remove_SessionOwnWorktree_OwnerSessionStillLive_RefusesEvenForItsOwnAgent()
    {
        // The other half of the same fix: a worktree the UI created (IsAgentCreated stays false, the default) is
        // the working directory the session is actually running in — that stays refused even when the caller is
        // that very session, unlike the agent-made case above.
        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record("my-pane", "/wt/session-own");
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var live = Substitute.For<ILiveSessionRegistry>();
        live.LiveSessionIds.Returns(new HashSet<string>(StringComparer.Ordinal) { "my-pane" });
        var tools = new WorktreeTools(manager, live);

        McpRequestContext.Set("my-pane");
        try
        {
            using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/session-own"));

            Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
            await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task Remove_DirtyWorktree_ConsentApproved_RemovesWithForce()
    {
        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record("gone-pane", "/wt/dirty");
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        manager.HasUncommittedChangesAsync(record, Arg.Any<CancellationToken>()).Returns(true);
        var consent = Substitute.For<IConsentBroker>();
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(ConsentOutcome.Approved));
        var tools = new WorktreeTools(manager, liveSessions: null, consent: consent);

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/dirty"));

        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        await manager.Received(1).RemoveAsync(record, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_DirtyWorktree_ConsentDenied_RemovesNothing()
    {
        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record("gone-pane", "/wt/dirty");
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        manager.HasUncommittedChangesAsync(record, Arg.Any<CancellationToken>()).Returns(true);
        var consent = Substitute.For<IConsentBroker>();
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(ConsentOutcome.Denied));
        var tools = new WorktreeTools(manager, liveSessions: null, consent: consent);

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/dirty"));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_DirtyWorktree_NoConsentSurface_Refuses()
    {
        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record("gone-pane", "/wt/dirty");
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        manager.HasUncommittedChangesAsync(record, Arg.Any<CancellationToken>()).Returns(true);
        var tools = new WorktreeTools(manager);

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/dirty"));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
