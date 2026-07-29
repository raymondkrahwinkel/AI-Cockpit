using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// That a failed delegation still says <em>why</em> by the time anyone asks. One failure finishes a task twice —
/// a <c>SessionError</c> that knows the reason, then the turn's own completion, which reports failure carrying no
/// diagnostic of its own (<c>diagnostic</c> is <c>turn.Result</c>, and a provider that refuses a turn outright
/// sends none). Plain assignment let the second call erase the first, so every failure read as <c>error: null</c>
/// to the operator and to <c>get_task_result</c> — undiagnosable, though the reason had been in hand a millisecond
/// earlier. Found on a real one: the Codex profile refusing with "You've hit your usage limit".
/// <para>
/// The mirror case is pinned too, because it is what stops the obvious fix from being wrong: a follow-up turn
/// reuses the same entry, so a task that has since answered must not still carry the failure it recovered from.
/// Keeping the reason unconditionally would trade an empty error for a stale one.
/// </para>
/// </summary>
public class DelegationFailureReasonTests
{
    private const string UsageLimit = "You've hit your usage limit. Upgrade to Plus to continue using Codex.";

    [Fact]
    public async Task AFailedTurnWithNoDiagnosticOfItsOwn_KeepsTheReasonTheSessionErrorGave()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_RefusedThenFailedTurnWithoutADiagnostic());
        var service = _Service(driver);

        var task = await service.DelegateAsync(new DelegationRequest("local", "review this"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.TurnCount >= 1);

        var finished = service.GetTask(task.TaskId)!;
        finished.Status.Should().Be(DelegatedTaskStatus.Failed);
        finished.Error.Should().Be(UsageLimit, "the turn that followed carried no reason of its own to replace it with");
    }

    [Fact]
    public async Task AFailedTurnThatDoesBringItsOwnDiagnostic_ReplacesTheEarlierReason()
    {
        // Later and better beats earlier: this is not "first reason wins", it is "a reason is never traded for none".
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_RefusedThenFailedTurnWithADiagnostic());
        var service = _Service(driver);

        var task = await service.DelegateAsync(new DelegationRequest("local", "review this"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.TurnCount >= 1);

        service.GetTask(task.TaskId)!.Error.Should().Be("The model ran out of context.");
    }

    [Fact]
    public async Task ATurnThatSucceedsAfterASessionError_ClearsTheReason_SoAnAnsweredTaskCarriesNoStaleFailure()
    {
        // A SessionError is not proof a session is over (AC-106) — a follow-up turn on the same entry can still
        // answer. Were the reason kept unconditionally, this task would report success and a failure at once.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_ErrorThenASuccessfulTurn());
        var service = _Service(driver);

        var task = await service.DelegateAsync(new DelegationRequest("local", "review this"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.TurnCount >= 1);

        var finished = service.GetTask(task.TaskId)!;
        finished.Status.Should().Be(DelegatedTaskStatus.Completed);
        finished.Error.Should().BeNull();

        // Deliberately not asserted: that Result is "Reviewed it.". It is null here, and that is a separate defect
        // this change does not touch. The SessionError branch finishes with keepSessionAlive left at false, which
        // drops Runtime — so the assistant text of any turn after it has nowhere to be recorded, and the task
        // answers with an empty result. Adjacent to the comment two lines above it in DelegationService, which says
        // a SessionError is not proof the session is over; it holds the worktree open on that reasoning but lets
        // the runtime go anyway. Left flagged rather than fixed in passing: changing it moves session teardown and
        // idle reaping, which is a wider blast radius than a lost error message.
    }

    private static async IAsyncEnumerable<SessionEvent> _RefusedThenFailedTurnWithoutADiagnostic()
    {
        yield return new SessionError { SessionId = "s1", Message = UsageLimit };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "error", Result = null, IsError = true };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private static async IAsyncEnumerable<SessionEvent> _RefusedThenFailedTurnWithADiagnostic()
    {
        yield return new SessionError { SessionId = "s1", Message = UsageLimit };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "error", Result = "The model ran out of context.", IsError = true };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private static async IAsyncEnumerable<SessionEvent> _ErrorThenASuccessfulTurn()
    {
        yield return new SessionError { SessionId = "s1", Message = UsageLimit };
        yield return new AssistantTextCompleted { SessionId = "s1", Text = "Reviewed it." };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "Reviewed it.", IsError = false };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private static DelegationService _Service(ISessionDriver driver)
    {
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, TimeoutMinutes: 0));

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);

        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);

        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([new McpServerConfig { Name = "filesystem", Enabled = true }]);

        return new DelegationService(
            profileStore,
            new SessionManager(driverFactory),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMilliseconds(minutes * 30));
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }
}
