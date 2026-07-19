using System.Text.Json;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.Plugins.Abstractions.Consent;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Worktrees;

/// <summary>
/// The agent-facing worktree MCP tools (AC-104): thin over <see cref="IWorktreeManager"/>, returning the path/branch
/// on create, refusing a remove of a path it does not manage or a live session's tree, and gating a dirty removal
/// behind operator consent.
/// </summary>
public class WorktreeToolsTests
{
    private static WorktreeRecord _Record(string session, string path) =>
        new(session, "/repo", path, "cockpit/x", "abc", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Create_ReturnsThePathAndBranch()
    {
        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record("pane", "/wt/path");
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
        var record = _Record("live-pane", "/wt/live");
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var live = Substitute.For<ILiveSessionRegistry>();
        live.LiveSessionIds.Returns(new HashSet<string>(StringComparer.Ordinal) { "live-pane" });
        var tools = new WorktreeTools(manager, live);

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/live"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_CleanWorktree_RemovesWithoutForceOrConsent()
    {
        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record("gone-pane", "/wt/gone");
        manager.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        manager.HasUncommittedChangesAsync(record, Arg.Any<CancellationToken>()).Returns(false);
        var live = Substitute.For<ILiveSessionRegistry>();
        live.LiveSessionIds.Returns(new HashSet<string>(StringComparer.Ordinal));
        var consent = Substitute.For<IConsentBroker>();
        var tools = new WorktreeTools(manager, live, consent);

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/gone"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        await manager.Received(1).RemoveAsync(record, false, Arg.Any<CancellationToken>());
        await consent.DidNotReceive().RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>());
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

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
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

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
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

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
