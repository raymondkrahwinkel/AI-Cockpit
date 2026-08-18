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
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// The `_tasks` eviction policy (AC-880): a finished entry — carrying the full result text a caller may still
/// want — is swept only once it has sat well past the point a caller could reasonably still be collecting it.
/// </summary>
public class DelegationEvictionTests
{
    [Fact]
    public async Task ATaskFinishedLongAgo_IsEvictedOnTheNextDelegation()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamCompletingATurn());
        var service = _Service(driver, retentionMs: 30);

        var old = await service.DelegateAsync(new DelegationRequest("local", "first"));
        await _WaitUntilAsync(() => service.GetTask(old.TaskId)!.Status == DelegatedTaskStatus.Completed);

        // Well past the (millisecond) retention window for this test.
        await Task.Delay(200);

        // Only a new delegation sweeps — nothing evicts on a passive read.
        driver.Events.Returns(_StreamThatNeverFinishes());
        await service.DelegateAsync(new DelegationRequest("local", "second"));

        Assert.Null(service.GetTask(old.TaskId));
    }

    [Fact]
    public async Task ATaskJustFinished_StillHasItsResultAfterAnotherDelegation()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamCompletingATurn());
        var service = _Service(driver, retentionMs: 60_000);

        var justFinished = await service.DelegateAsync(new DelegationRequest("local", "first"));
        await _WaitUntilAsync(() => service.GetTask(justFinished.TaskId)!.Status == DelegatedTaskStatus.Completed);

        driver.Events.Returns(_StreamThatNeverFinishes());
        await service.DelegateAsync(new DelegationRequest("local", "second"));

        var stillThere = service.GetTask(justFinished.TaskId);
        Assert.NotNull(stillThere);
        Assert.Equal(DelegatedTaskStatus.Completed, stillThere!.Status);
        Assert.Equal("done", stillThere.Result);
    }

    private static DelegationService _Service(ISessionDriver driver, int retentionMs)
    {
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, MaxConcurrent: 5));

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
            minutes => TimeSpan.FromMinutes(minutes),
            taskRetention: TimeSpan.FromMilliseconds(retentionMs));
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    private static async IAsyncEnumerable<SessionEvent> _StreamCompletingATurn()
    {
        yield return new AssistantTextCompleted { SessionId = "s1", Text = "done" };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = null, IsError = false };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private static async IAsyncEnumerable<SessionEvent> _StreamThatNeverFinishes(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
