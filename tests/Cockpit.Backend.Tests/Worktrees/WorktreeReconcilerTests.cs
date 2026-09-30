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

    // Criterion 2: a session that disappeared since the last tick is simply absent from the next tick's live set,
    // so the crash net picks its worktree up without waiting for an app restart.
    [Fact]
    public async Task ASessionGoneSinceTheLastTick_IsNoLongerLiveOnTheNextOne()
    {
        var live = new List<string> { "pane-1", "pane-2" };
        // A fresh list per tick, like the cockpit's own `AllSessions().Select(...).ToList()` — handing the same
        // instance twice would leave both recorded calls pointing at whatever it says now.
        using var reconciler = new WorktreeReconciler(_worktrees) { LiveSessionIds = () => live.ToList() };

        await reconciler.RunOnceAsync();
        live.Remove("pane-2");
        await reconciler.RunOnceAsync();

        await _worktrees.Received(1).ReconcileAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("pane-2")),
            Arg.Any<CancellationToken>());
        await _worktrees.Received(1).ReconcileAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => !ids.Contains("pane-2") && ids.Contains("pane-1")),
            Arg.Any<CancellationToken>());
    }

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
