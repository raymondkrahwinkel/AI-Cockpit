using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Core.Tests.Worktrees;

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

    // Criterion 1: a tick costs one `ReconcileAsync` and nothing else — no list of its own to keep, so nothing here
    // grows over a long run.
    [Fact]
    public async Task ATickWithNothingToDo_IsOneReconcileAndNoMore()
    {
        using var reconciler = new WorktreeReconciler(_worktrees) { LiveSessionIds = () => ["pane-1"] };

        await reconciler.RunOnceAsync();

        await _worktrees.Received(1).ReconcileAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    // Nothing wired means no live set, and an empty live set would read as "every worktree is an orphan" — the one
    // way this could destroy work, so it sweeps nothing at all instead.
    [Fact]
    public async Task WithNothingWiredToSweepAgainst_ItDoesNothing()
    {
        using var reconciler = new WorktreeReconciler(_worktrees);

        await reconciler.RunOnceAsync();

        await _worktrees.DidNotReceiveWithAnyArgs().ReconcileAsync(default!, default);
    }

    // Asked of the container rather than of the class: an unregistered reconciler resolves to null in `App.axaml.cs`,
    // which starts nothing — the timer dead with every test still green.
    [Fact]
    public async Task TheContainer_ResolvesTheReconciler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Core.DependencyInjection).Assembly,
            typeof(Infrastructure.DependencyInjection).Assembly,
            typeof(WorktreeReconciler).Assembly);

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<WorktreeReconciler>());
    }
}
