using System.Runtime.CompilerServices;
using System.Text.Json;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Worktrees;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// A delegated task's worktree, from the two sides that used to be missing (AC-106). A delegated session runs
/// headless — it has no pane — so the cockpit's live-session registry never knew it existed: its checkout read as
/// abandoned, which the worktree guards take as "safe to remove", and nothing released it when the task ended, so it
/// sat there until the next startup reconcile swept it.
/// <para>
/// The two halves belong together: a task guards its checkout for as long as it holds a session, and every path
/// that lets go of that session hands the checkout back — so no path leaves it both unguarded and uncollected.
/// </para>
/// </summary>
public class DelegationWorktreeCleanupTests
{
    [Fact]
    public async Task AWorktreeOfARunningDelegatedTask_IsRefusedByTheSameGuardThatProtectsAPane()
    {
        var service = _Service(_StreamThatNeverFinishes(), out _);
        var task = await service.DelegateAsync(new DelegationRequest("local", "long job"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Running);

        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record(task.TaskId, "/wt/delegated");
        manager.ListAsync(default).Returns(new List<WorktreeRecord> { record });
        var tools = new WorktreeTools(manager, new LiveSessionRegistry([service]));

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/delegated"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("error").GetString().Should().Contain("still running");
        _Removals(manager).Should().BeEmpty();
    }

    [Fact]
    public async Task AWorktreeOfAStoppedDelegatedTask_IsRemovableAgain()
    {
        // The other half of the guard: it must let go. A registry that reported every task it had ever run would
        // block the operator's own cleanup for the rest of the app's life, which is a worse failure than the one
        // this fixes — and a test that only asserts the refusal cannot tell the two apart.
        var service = _Service(_StreamThatNeverFinishes(), out _);
        var task = await service.DelegateAsync(new DelegationRequest("local", "long job"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Running);
        await service.StopAsync(task.TaskId);

        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record(task.TaskId, "/wt/delegated");
        manager.ListAsync(default).Returns(new List<WorktreeRecord> { record });
        manager.HasUncommittedChangesAsync(record, default).Returns(false);
        var tools = new WorktreeTools(manager, new LiveSessionRegistry([service]));

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/delegated"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        _Removals(manager).Should().Equal((record, false));
    }

    [Fact]
    public async Task ATaskThatAnsweredButHasNotBeenReaped_StillHoldsItsWorktree()
    {
        // The distinction the guard is built on, and the one a "is it Running?" check would get wrong: this task is
        // Completed, yet its session is deliberately still up so the orchestrator can follow up — and a follow-up
        // works in that same directory. Its checkout is not free until the reap says so.
        var service = _Service(_StreamCompletingATurn(), out var worktrees, idleWindow: TimeSpan.FromMinutes(5));
        var task = await service.DelegateAsync(new DelegationRequest("local", "quick work"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Completed);

        // Nor has it been handed back — the reap does that, not the answer. Asserted here rather than in the test
        // that exercises the reap, because a five-minute window leaves no doubt about which side of it we are on.
        _ReleasedSessions(worktrees).Should().BeEmpty();

        var manager = Substitute.For<IWorktreeManager>();
        var record = _Record(task.TaskId, "/wt/answered");
        manager.ListAsync(default).Returns(new List<WorktreeRecord> { record });
        var tools = new WorktreeTools(manager, new LiveSessionRegistry([service]));

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/answered"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        _Removals(manager).Should().BeEmpty();
    }

    [Fact]
    public async Task ATaskTheDriverReportedAnErrorOn_KeepsItsWorktree()
    {
        // The one ending that must NOT hand the checkout back, and the reason is that the event lies about being an
        // ending: every plugin-side error becomes a SessionError, including notices from a session that is running
        // fine — the cockpit falling behind on events, or a driver saying it could not apply a system prompt. A
        // release here removes a momentarily clean worktree while its sub-agent is still working in it, and it would
        // spend the task's one release on a session that has not finished.
        // The error is held back until the task is properly running. Raising it while the start is still unwinding is
        // not the case being described — a session reports an error once it is up — and it races the start path, so
        // the test would be measuring that race rather than the rule.
        var errorArrives = new TaskCompletionSource();
        var service = _Service(_StreamRaisingADriverError(errorArrives.Task), out var worktrees);
        var task = await service.DelegateAsync(new DelegationRequest("local", "reports an error but carries on"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Running);

        errorArrives.SetResult();

        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Failed);
        await Task.Delay(150);

        _ReleasedSessions(worktrees).Should().BeEmpty();
    }

    [Fact]
    public async Task ATaskThatCouldNotBeStarted_AlsoHandsBackItsWorktree()
    {
        // Usually there is nothing to hand back here — the task never got far enough to ask for a worktree — and the
        // call is a no-op. It is made anyway so the rule has no exceptions to remember, and this pins that: reasoning
        // about how far a failed start got is the kind of assumption that is right until the day it is not.
        var service = _Service(
            _StreamThatNeverFinishes(),
            out var worktrees,
            startFailure: new InvalidOperationException("the provider could not be reached"));

        var task = await service.DelegateAsync(new DelegationRequest("local", "never gets going"));

        service.GetTask(task.TaskId)!.Error.Should().Contain("could not be reached");
        service.GetTask(task.TaskId)!.Status.Should().Be(DelegatedTaskStatus.Failed);
        _ReleasedSessions(worktrees).Should().Equal(task.TaskId);
    }

    [Fact]
    public async Task TwoClosingPathsOnTheSameTask_HandTheWorktreeBackOnce()
    {
        // Two of these paths can land together — an idle reap whose delay has already elapsed cannot be cancelled by
        // a stop arriving at that instant — and both would release the same checkout. Stopping twice is the
        // deterministic stand-in: it drives the same claim, which is the thing that has to hold.
        var service = _Service(_StreamThatNeverFinishes(), out var worktrees);
        var task = await service.DelegateAsync(new DelegationRequest("local", "long job"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Running);

        await service.StopAsync(task.TaskId);
        await service.StopAsync(task.TaskId);

        _ReleasedSessions(worktrees).Should().Equal(task.TaskId);
    }

    [Fact]
    public async Task StopAsync_HandsTheTasksWorktreeBackToTheCleanupPolicy()
    {
        // ReleaseAsync and not RemoveAsync, deliberately: that one call *is* the policy a closing pane goes through
        // (CockpitViewModel.CloseSessionAsync), so a clean checkout goes with its branch and one holding work is
        // retained. Reaching past it would mean a second, drifting copy of that decision.
        var service = _Service(_StreamThatNeverFinishes(), out var worktrees);
        var task = await service.DelegateAsync(new DelegationRequest("local", "long job"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Running);

        await service.StopAsync(task.TaskId);

        _ReleasedSessions(worktrees).Should().Equal(task.TaskId);
    }

    [Fact]
    public async Task ARunningTask_KeepsItsWorktree()
    {
        var service = _Service(_StreamThatNeverFinishes(), out var worktrees);
        var task = await service.DelegateAsync(new DelegationRequest("local", "long job"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Running);

        await Task.Delay(150);

        _ReleasedSessions(worktrees).Should().BeEmpty();
    }

    [Fact]
    public async Task ATaskThatAnswered_KeepsItsWorktreeUntilTheIdleWindowCloses()
    {
        // A task that answered keeps its session so the orchestrator can follow up — and a follow-up puts it straight
        // back to work in that directory. Releasing on the answer would pull the checkout out from under the turn
        // after it; the reap is the right moment, and this pins both sides of it in one test.
        var service = _Service(_StreamCompletingATurn(), out var worktrees, idleWindow: TimeSpan.FromMilliseconds(250));
        var task = await service.DelegateAsync(new DelegationRequest("local", "quick work"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Completed);

        await _WaitUntilAsync(() => worktrees.ReceivedCalls().Any(), attempts: 200);
        _ReleasedSessions(worktrees).Should().Equal(task.TaskId);
    }

    [Fact]
    public async Task ATaskStoppedByItsProfilesTimeout_AlsoHandsBackItsWorktree()
    {
        // The third way a delegated session ends. It is not a follow-up-able state — the session is torn down and no
        // reap is armed — so if this path did not release, a task that ran too long would leave its checkout behind
        // precisely when nobody is watching, which is the case the timeout exists for.
        var service = _Service(_StreamThatNeverFinishes(), out var worktrees, timeoutMinutes: 1);
        var task = await service.DelegateAsync(new DelegationRequest("local", "loop forever"));

        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Failed);

        await _WaitUntilAsync(() => worktrees.ReceivedCalls().Any());
        _ReleasedSessions(worktrees).Should().Equal(task.TaskId);
    }

    [Fact]
    public async Task TheConstructorTheAppActuallyUses_CarriesTheWorktreeManagerThrough()
    {
        // Every other test here builds the service through the internal test seam, which takes the manager as its
        // own argument. The app builds it through the public constructor, which forwards it — and a forward that
        // quietly passed null would leave all of this working in the tests and doing nothing in the cockpit.
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true));

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(default).Returns([profile]);
        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(default).Returns([new McpServerConfig { Name = "filesystem", Enabled = true }]);
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamThatNeverFinishes());
        var worktrees = Substitute.For<IWorktreeManager>();

        var service = new DelegationService(
            profileStore,
            new SessionManager(new StubDriverFactory(driver, failure: null)),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            NoSessionWorkspaces.Instance,
            worktrees: worktrees);

        var task = await service.DelegateAsync(new DelegationRequest("local", "long job"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Running);
        await service.StopAsync(task.TaskId);

        _ReleasedSessions(worktrees).Should().Equal(task.TaskId);
    }

    [Fact]
    public void TheRegistryFoldsThePanesAndTheHeadlessSourcesTogether()
    {
        // The panes were the only source before, so the fold has to keep reporting them: a registry that answered
        // only with delegated ids would unprotect every worktree the operator's own sessions are sitting in.
        var delegated = Substitute.For<ILiveSessionSource>();
        delegated.LiveSessionIds.Returns(new HashSet<string>(StringComparer.Ordinal) { "task-1" });
        var registry = new LiveSessionRegistry([delegated]);
        registry.SetSource(() => new HashSet<string>(StringComparer.Ordinal) { "pane-1" });

        registry.LiveSessionIds.Should().BeEquivalentTo(["pane-1", "task-1"]);
    }

    // startFailure makes the session fail to start, by way of a factory that cannot produce a driver — a real class
    // rather than a configured substitute. Telling a substitute to throw goes through NSubstitute's ambient
    // argument-matcher state, which xUnit's parallel classes share; the test then passes on its own and fails in the
    // suite, which is the worst kind of green.
    private static DelegationService _Service(
        IAsyncEnumerable<SessionEvent> events,
        out IWorktreeManager worktrees,
        int timeoutMinutes = 0,
        TimeSpan? idleWindow = null,
        Exception? startFailure = null)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(events);

        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, TimeoutMinutes: timeoutMinutes));

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(default).Returns([profile]);

        var driverFactory = new StubDriverFactory(driver, startFailure);

        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(default).Returns([new McpServerConfig { Name = "filesystem", Enabled = true }]);

        worktrees = Substitute.For<IWorktreeManager>();

        // The profile's "minutes" become milliseconds here, so a test exercises the real timer rather than waiting
        // a real minute for it — the seam DelegationTimeoutTests already uses, with the idle window alongside it.
        return new DelegationService(
            profileStore,
            new SessionManager(driverFactory),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMilliseconds(minutes * 30),
            worktrees: worktrees,
            idleWindow: idleWindow);
    }

    private sealed class StubDriverFactory(ISessionDriver driver, Exception? failure) : ISessionDriverFactory
    {
        public ISessionDriver Create(SessionProfile? profile) => failure is null ? driver : throw failure;
    }

    // Everything here reads the recorded calls instead of going through Received()/DidNotReceive(), and configures
    // the substitutes with plain values instead of Arg matchers. Both of those park state on the calling thread for
    // the call that follows it, and xUnit runs this class beside others doing the same — which now and then makes an
    // assertion here answer about a substitute that is not ours. Two tests in this file flaked exactly that way:
    // green alone, red in the suite, and red or green depending on whether a diagnostic line was present.
    private static IReadOnlyList<string> _ReleasedSessions(IWorktreeManager worktrees) =>
        [.. _CallsTo(worktrees, nameof(IWorktreeManager.ReleaseAsync)).Select(arguments => (string)arguments[0]!)];

    private static IReadOnlyList<(WorktreeRecord Record, bool Force)> _Removals(IWorktreeManager manager) =>
        [.. _CallsTo(manager, nameof(IWorktreeManager.RemoveAsync))
            .Select(arguments => ((WorktreeRecord)arguments[0]!, (bool)arguments[1]!))];

    private static IEnumerable<object?[]> _CallsTo(IWorktreeManager manager, string method) =>
        manager.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == method)
            .Select(call => call.GetArguments());

    private static WorktreeRecord _Record(string sessionId, string path) =>
        new(sessionId, "/repo", path, "wt/branch", "abc123", DateTimeOffset.Now);

    private static async Task _WaitUntilAsync(Func<bool> condition, int attempts = 100)
    {
        for (var attempt = 0; attempt < attempts && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    private static async IAsyncEnumerable<SessionEvent> _StreamThatNeverFinishes(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<SessionEvent> _StreamRaisingADriverError(
        Task errorArrives,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await errorArrives.WaitAsync(cancellationToken);
        yield return new SessionError { SessionId = "s1", Message = "the provider dropped the connection" };
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    // Stays open after the turn, as a real session does — the answer does not end the session, the reap does. The
    // wait honours the token the runtime cancels on teardown, so disposing actually completes; an uncancellable one
    // leaves the event pump running and the stop never returns.
    private static async IAsyncEnumerable<SessionEvent> _StreamCompletingATurn(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AssistantTextCompleted { SessionId = "s1", Text = "done" };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = null, IsError = false };
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
