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
/// The false-success guard (AC-100/AC-110). The local-model driver reports a turn as "success" whenever its HTTP
/// stream ends cleanly — including when every tool call it made was denied by the delegated permission gate (a
/// denial comes back as an error tool result, not an exception) and it produced nothing. Left unguarded, such a
/// no-op run is relayed to the caller as <c>Completed</c> with the model's apology as the result, and a naive
/// consumer reports progress that never happened. These pin that a turn which ran tools but landed none of them is
/// surfaced as <c>Failed</c> with a diagnostic, while a legitimate no-tool text answer stays <c>Completed</c>.
/// </summary>
public class DelegationFalseSuccessTests
{
    [Fact]
    public async Task ATurnWhoseOnlyToolCallsWereDenied_IsFailedWithADiagnostic_NotSilentlyCompleted()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamWithDeniedToolThenSuccess());
        var service = _Service(driver);

        var task = await service.DelegateAsync(new DelegationRequest("local", "write a file"));
        await _WaitUntilAsync(() => _IsFinished(service.GetTask(task.TaskId)!.Status));

        var finished = service.GetTask(task.TaskId)!;
        finished.Status.Should().Be(DelegatedTaskStatus.Failed, "a run that landed no tool call produced nothing");
        finished.Error.Should().Contain("No-op run");
        // The model's own reply is still preserved so the caller can see what it said, not just that it failed.
        finished.Result.Should().Be("I can't create files.");
    }

    [Fact]
    public async Task ATurnWithASuccessfulToolCall_IsCompleted()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamWithSuccessfulTool());
        var service = _Service(driver);

        var task = await service.DelegateAsync(new DelegationRequest("local", "write a file"));
        await _WaitUntilAsync(() => _IsFinished(service.GetTask(task.TaskId)!.Status));

        var finished = service.GetTask(task.TaskId)!;
        finished.Status.Should().Be(DelegatedTaskStatus.Completed);
        finished.Error.Should().BeNull();
    }

    [Fact]
    public async Task ATurnThatUsedNoTools_StaysCompleted_SoAPlainTextAnswerIsNotAFalseFailure()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamPlainTextAnswer());
        var service = _Service(driver);

        var task = await service.DelegateAsync(new DelegationRequest("local", "just answer"));
        await _WaitUntilAsync(() => _IsFinished(service.GetTask(task.TaskId)!.Status));

        var finished = service.GetTask(task.TaskId)!;
        finished.Status.Should().Be(DelegatedTaskStatus.Completed);
        finished.Error.Should().BeNull();
        finished.Result.Should().Be("The answer is 42.");
    }

    [Fact]
    public async Task AFollowUpTextTurnAfterADeniedTurn_IsJudgedOnItsOwn_NotInheritedAsFailure()
    {
        // Per-turn counters (AC-100 review): turn 1's denied tool call must not make a later plain-text turn look
        // like a no-op run. Without the per-turn reset, the follow-up inherits turn 1's denial and is wrongly Failed.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_TwoTurns_DeniedThenPlainText());
        var service = _Service(driver);

        var task = await service.DelegateAsync(new DelegationRequest("local", "write then chat"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.TurnCount >= 2);

        var finished = service.GetTask(task.TaskId)!;
        finished.Status.Should().Be(DelegatedTaskStatus.Completed, "the second, tool-less turn stands on its own");
        finished.Error.Should().BeNull();
    }

    [Fact]
    public async Task ADeniedFollowUpTurnAfterASuccessfulTurn_IsFailed_NotHiddenAsSuccess()
    {
        // The mirror case: turn 1 lands a tool call, the follow-up's only tool call is denied. Session-cumulative
        // counters would still see one success and report Completed, hiding the failed follow-up; per-turn counters
        // catch it.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_TwoTurns_SuccessThenDenied());
        var service = _Service(driver);

        var task = await service.DelegateAsync(new DelegationRequest("local", "write then write"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.TurnCount >= 2);

        service.GetTask(task.TaskId)!.Status.Should().Be(DelegatedTaskStatus.Failed);
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

    private static bool _IsFinished(DelegatedTaskStatus status) =>
        status is DelegatedTaskStatus.Completed or DelegatedTaskStatus.Failed or DelegatedTaskStatus.Stopped;

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    private static async IAsyncEnumerable<SessionEvent> _StreamWithDeniedToolThenSuccess()
    {
        yield return new ToolUseRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "write_file", InputJson = "{}" };
        yield return new ToolResult { SessionId = "s1", ToolUseId = "t1", Content = "Tool 'write_file' was blocked.", IsError = true };
        yield return new AssistantTextCompleted { SessionId = "s1", Text = "I can't create files." };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "I can't create files.", IsError = false };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private static async IAsyncEnumerable<SessionEvent> _StreamWithSuccessfulTool()
    {
        yield return new ToolUseRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "write_file", InputJson = "{}" };
        yield return new ToolResult { SessionId = "s1", ToolUseId = "t1", Content = "wrote 1 file", IsError = false };
        yield return new AssistantTextCompleted { SessionId = "s1", Text = "Done." };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "Done.", IsError = false };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private static async IAsyncEnumerable<SessionEvent> _StreamPlainTextAnswer()
    {
        yield return new AssistantTextCompleted { SessionId = "s1", Text = "The answer is 42." };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "The answer is 42.", IsError = false };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private static async IAsyncEnumerable<SessionEvent> _TwoTurns_DeniedThenPlainText()
    {
        // Turn 1: a denied tool call → Failed.
        yield return new ToolUseRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "write_file", InputJson = "{}" };
        yield return new ToolResult { SessionId = "s1", ToolUseId = "t1", Content = "write_file was blocked.", IsError = true };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "I can't create files.", IsError = false };
        // Turn 2: a plain-text answer, no tools → must be Completed on its own, not inheriting turn 1's denial.
        yield return new AssistantTextCompleted { SessionId = "s1", Text = "Sure, here is the explanation." };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "Sure, here is the explanation.", IsError = false };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private static async IAsyncEnumerable<SessionEvent> _TwoTurns_SuccessThenDenied()
    {
        // Turn 1: a successful tool call → Completed.
        yield return new ToolUseRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "write_file", InputJson = "{}" };
        yield return new ToolResult { SessionId = "s1", ToolUseId = "t1", Content = "wrote 1 file", IsError = false };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "Done.", IsError = false };
        // Turn 2: a denied tool call, nothing landed → must flip back to Failed rather than stay Completed.
        yield return new ToolUseRequested { SessionId = "s1", ToolUseId = "t2", ToolName = "write_file", InputJson = "{}" };
        yield return new ToolResult { SessionId = "s1", ToolUseId = "t2", Content = "write_file was blocked.", IsError = true };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "I can't create files.", IsError = false };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }
}
