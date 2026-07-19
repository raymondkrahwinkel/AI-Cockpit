using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// A per-task least-privilege cap on <c>delegate_task</c> (AC-117). A caller can ask for a lower permission for one
/// task than the profile's ceiling — but never a higher one: the effective ceiling the delegated tool gate runs
/// under is the more restrictive of the profile's and the request's, so a request can only ever narrow what the
/// operator already granted. <c>DelegateAsync</c> awaits the start, so the gate is armed by the time it returns.
/// </summary>
public class DelegationPermissionClampTests
{
    [Fact]
    public async Task ARequestBelowTheCeiling_GatesAtTheRequest()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, ceiling: "bypassPermissions");

        await service.DelegateAsync(new DelegationRequest("local", "review only", RequestedPermission: "acceptEdits"));

        await driver.Received().SetDelegatedToolGateAsync("acceptEdits", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARequestAboveTheCeiling_IsClampedToTheCeiling()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, ceiling: "default");

        await service.DelegateAsync(new DelegationRequest("local", "do everything", RequestedPermission: "bypassPermissions"));

        await driver.Received().SetDelegatedToolGateAsync("default", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoRequest_GatesAtTheProfileCeiling()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, ceiling: "acceptEdits");

        await service.DelegateAsync(new DelegationRequest("local", "work"));

        await driver.Received().SetDelegatedToolGateAsync("acceptEdits", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    private static DelegationService _ServiceWith(ISessionDriver driver, string ceiling)
    {
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, PermissionCeiling: ceiling));

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

    private static async IAsyncEnumerable<SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
