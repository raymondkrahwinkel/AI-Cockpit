using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Worktrees;
using NSubstitute;

namespace Cockpit.Backend.Tests.Worktrees;

// AC-643. The tick, not the policy: every test here drives `RunOnceAsync` and only checks which live set reached
// `ReconcileAsync` — what it then removes or retains is `WorktreeManagerTests`' business, not this one's.
public class WorktreeReconcilerTests
{
    private readonly IWorktreeManager _worktrees = Substitute.For<IWorktreeManager>();

    // AC-654: the assistant owns every worktree `worktree_create` makes for it and is in no session list, so a live
    // set that does not name it makes each of those an orphan the sweep removes under a working agent.
    [Fact]
    public async Task TheAssistant_IsLiveEvenWhenTheWiredSetLeavesItOut()
    {
        using var reconciler = new WorktreeReconciler(_worktrees) { LiveSessionIds = () => ["pane-1"] };

        await reconciler.RunOnceAsync();

        await _worktrees.Received(1).ReconcileAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains(AssistantIdentity.PaneId) && ids.Contains("pane-1")),
            Arg.Any<CancellationToken>());
    }

}
