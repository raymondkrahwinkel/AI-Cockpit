using System.Runtime.CompilerServices;
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
/// The per-task timeout (#67). Nobody is watching a delegated session: a model that loops, waits on something
/// that never comes, or simply grinds on would hold the profile's only slot — and keep drawing on its provider —
/// until the app closes. Just as important is the other half: a task that answered in time must never be stopped
/// after the fact by a timer nobody cancelled.
/// </summary>
public class DelegationTimeoutTests
{
    [Fact]
    public async Task ATaskThatOutlivesItsProfilesLimit_IsStoppedAndReported()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamThatNeverFinishes());
        var audit = Substitute.For<IDelegationAuditLog>();
        var service = _Service(driver, audit, timeoutMinutes: 1);

        var task = await service.DelegateAsync(new DelegationRequest("local", "loop forever"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Failed);

        var timedOut = service.GetTask(task.TaskId)!;
        timedOut.Status.Should().Be(DelegatedTaskStatus.Failed);
        timedOut.Error.Should().Contain("ran longer than");

        // Stopped for real, not just marked: the session is torn down, so the slot and the provider are freed.
        await driver.Received(1).DisposeAsync();
        await audit.Received(1).RecordAsync(
            Arg.Is<DelegationAuditEntry>(entry => entry.Action == DelegationAuditAction.TimedOut),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ATaskThatAnswersInTime_IsNotStoppedAfterwards()
    {
        // The timer is cancelled when the turn completes. Without that, a task that answered in seconds would be
        // torn down minutes later — and a follow-up to it would find no session.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamCompletingATurn());
        var service = _Service(driver, Substitute.For<IDelegationAuditLog>(), timeoutMinutes: 1);

        var task = await service.DelegateAsync(new DelegationRequest("local", "quick work"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Completed);

        // Well past the (millisecond) timeout for this test.
        await Task.Delay(200);

        service.GetTask(task.TaskId)!.Status.Should().Be(DelegatedTaskStatus.Completed);
        await driver.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task AProfileWithNoTimeout_LetsATaskRunOn()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamThatNeverFinishes());
        var service = _Service(driver, Substitute.For<IDelegationAuditLog>(), timeoutMinutes: 0);

        var task = await service.DelegateAsync(new DelegationRequest("local", "long job"));
        await Task.Delay(200);

        service.GetTask(task.TaskId)!.Status.Should().Be(DelegatedTaskStatus.Running);
        await driver.DidNotReceive().DisposeAsync();
    }

    // The profile's "minutes" become milliseconds here, so the test exercises the real timer rather than waiting
    // a real minute for it.
    private static DelegationService _Service(ISessionDriver driver, IDelegationAuditLog audit, int timeoutMinutes)
    {
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, TimeoutMinutes: timeoutMinutes));

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
            audit,
            minutes => TimeSpan.FromMilliseconds(minutes * 30));
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
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

    private static async IAsyncEnumerable<SessionEvent> _StreamCompletingATurn()
    {
        yield return new AssistantTextCompleted { SessionId = "s1", Text = "done" };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = null, IsError = false };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }
}
