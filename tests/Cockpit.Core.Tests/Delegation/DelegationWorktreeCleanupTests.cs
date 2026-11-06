using System.Runtime.CompilerServices;
using System.Text.Json;
using Cockpit.Infrastructure.Sessions;
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
using Cockpit.Infrastructure.Worktrees;
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
        var tools = new WorktreeTools(manager, new LiveSessionRegistry(new SessionRegistry(), [service]));

        using var result = JsonDocument.Parse(await tools.RemoveAsync("/wt/delegated"));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("still running", result.RootElement.GetProperty("error").GetString());
        Assert.Empty(_Removals(manager));
    }

    [Fact]
    public async Task TwoClosingPathsOnTheSameTask_HandTheWorktreeBackOnce()
    {
        // Two of these paths can land together and both would release the same checkout; stopping twice is the
        // deterministic stand-in. It is also where "a stop hands the checkout back" is settled, on ReleaseAsync
        // rather than RemoveAsync — that one call *is* the policy a closing pane goes through.
        var service = _Service(_StreamThatNeverFinishes(), out var worktrees);
        var task = await service.DelegateAsync(new DelegationRequest("local", "long job"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Running);

        await service.StopAsync(task.TaskId);
        await service.StopAsync(task.TaskId);

        Assert.Equal(new[] { task.TaskId }, _ReleasedSessions(worktrees));
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
        Assert.Equal(new[] { task.TaskId }, _ReleasedSessions(worktrees));
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
