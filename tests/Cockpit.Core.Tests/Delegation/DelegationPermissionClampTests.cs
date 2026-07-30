using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// A per-task least-privilege cap on <c>delegate_task</c> (AC-117). A caller can ask for a lower permission for one
/// task than the profile's ceiling, always honoured outright. A request ABOVE the ceiling is either clamped
/// (no consent broker attached — the old, still-default behaviour) or put to the operator through the consent
/// broker, whose answer decides whether it runs at the requested level or clamped. <c>DelegateAsync</c> awaits
/// the start, so the gate is armed by the time it returns.
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
    public async Task ARequestAboveTheCeiling_WithNoConsentBroker_IsClampedToTheCeiling()
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

    [Fact]
    public async Task ARequestAboveTheCeiling_ApprovedByTheOperator_GatesAtTheRequest()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var consent = Substitute.For<IConsentBroker>();
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(ConsentOutcome.Approved));
        var auditLog = Substitute.For<IDelegationAuditLog>();
        var service = _ServiceWith(driver, ceiling: "default", consent, auditLog);

        await service.DelegateAsync(new DelegationRequest("local", "do everything", RequestedPermission: "bypassPermissions"));

        await driver.Received().SetDelegatedToolGateAsync("bypassPermissions", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await driver.Received().StartAsync(
            Arg.Any<SessionProfile>(),
            "bypassPermissions",
            Arg.Any<string>(),
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<string>(),
            Arg.Any<SessionResume>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await auditLog.Received().RecordAsync(
            Arg.Is<DelegationAuditEntry>(e => e.Action == DelegationAuditAction.PermissionElevated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARequestAboveTheCeiling_DeniedByTheOperator_IsClampedToTheCeiling()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var consent = Substitute.For<IConsentBroker>();
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(ConsentDecision.Denied);
        var auditLog = Substitute.For<IDelegationAuditLog>();
        var service = _ServiceWith(driver, ceiling: "default", consent, auditLog);

        await service.DelegateAsync(new DelegationRequest("local", "do everything", RequestedPermission: "bypassPermissions"));

        await driver.Received().SetDelegatedToolGateAsync("default", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await auditLog.Received().RecordAsync(
            Arg.Is<DelegationAuditEntry>(e => e.Action == DelegationAuditAction.PermissionElevationDenied),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARequestAboveTheCeiling_AsksAsADangerousActionThatIsNeverRemembered()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var consent = Substitute.For<IConsentBroker>();
        ConsentRequest? captured = null;
        consent.RequestConsentAsync(Arg.Do<ConsentRequest>(request => captured = request), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(ConsentOutcome.Approved));
        var service = _ServiceWith(driver, ceiling: "default", consent);

        await service.DelegateAsync(new DelegationRequest("local", "do everything", RequestedPermission: "bypassPermissions"));

        Assert.NotNull(captured);
        Assert.Equal(ConsentRisk.Dangerous, captured!.Risk);
        Assert.False(captured.AllowRemember);
        Assert.Equal("delegation.permission:local", captured.Scope);
    }

    [Fact]
    public async Task ARequestAtOrBelowTheCeiling_NeverAsksForConsent()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var consent = Substitute.For<IConsentBroker>();
        var service = _ServiceWith(driver, ceiling: "bypassPermissions", consent);

        await service.DelegateAsync(new DelegationRequest("local", "review only", RequestedPermission: "acceptEdits"));

        await consent.DidNotReceive().RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>());
    }

    // A same-rank alias ("plan" and "default" both mean read-only-unattended) must never read as an escalation:
    // the caller asked for no more than the profile already allows, so no prompt, in either direction.
    [Theory]
    [InlineData("default", "plan")]
    [InlineData("plan", "default")]
    public async Task ASameRankAliasRequest_NeverAsksForConsent(string ceiling, string requested)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var consent = Substitute.For<IConsentBroker>();
        var service = _ServiceWith(driver, ceiling, consent);

        await service.DelegateAsync(new DelegationRequest("local", "work", RequestedPermission: requested));

        await consent.DidNotReceive().RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// While a task's elevation prompt is unanswered, <c>Status</c> stays Queued (AC-117). Proves the queue
    /// drainer cannot pick that same task a second time and spawn it twice: freeing another slot on the same
    /// profile while the prompt is still open used to make <c>_StartNextQueuedAsync</c> see the queued-looking
    /// entry and start it again — a second session for one task. Without <see cref="DelegatedTaskEntry.TryClaimStart"/>
    /// this test fails on the driver-factory call count (3 instead of 2: one legitimate start each for A and B,
    /// plus B's phantom second start).
    /// </summary>
    [Fact]
    public async Task AQueuedElevationWait_IsNeverStartedTwiceByTheQueueDrainer()
    {
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(_ =>
        {
            var driver = Substitute.For<ISessionDriver>();
            driver.Events.Returns(_EmptyStream());
            return driver;
        });

        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, PermissionCeiling: "default", MaxConcurrent: 2, TimeoutMinutes: 0));

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);

        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([new McpServerConfig { Name = "filesystem", Enabled = true }]);

        var consentGate = new TaskCompletionSource<ConsentDecision>();
        var consent = Substitute.For<IConsentBroker>();
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(consentGate.Task);

        var service = new DelegationService(
            profileStore,
            new SessionManager(driverFactory),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMilliseconds(minutes * 30),
            consent: consent);

        // A occupies one of the profile's two slots and is never finished by its (empty) event stream, so it stays
        // Running until explicitly stopped below.
        var taskA = await service.DelegateAsync(new DelegationRequest("local", "prompt a", Label: "task a"));

        // B asks above the ceiling; its start blocks in _EffectiveCeilingAsync on the never-resolving consent gate,
        // so this call does not complete yet — run it without awaiting.
        var startB = service.DelegateAsync(new DelegationRequest("local", "prompt b", Label: "task b", RequestedPermission: "bypassPermissions"));

        // Give B's start a moment to reach (and suspend on) the consent await.
        await Task.Delay(50);
        Assert.Equal(DelegatedTaskStatus.Queued, service.ListTasks().First(t => t.Label == "task b").Status);

        // Frees A's slot and, at the end of StopAsync, runs the queue drainer — which used to see B (still
        // "Queued") as startable again while B's own start was still waiting on the same consent prompt.
        await service.StopAsync(taskA.TaskId);

        consentGate.SetResult(new ConsentDecision(ConsentOutcome.Approved));
        await startB;

        driverFactory.Received(2).Create(Arg.Any<SessionProfile?>());
    }

    private static DelegationService _ServiceWith(
        ISessionDriver driver, string ceiling, IConsentBroker? consent = null, IDelegationAuditLog? auditLog = null)
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
            auditLog ?? Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMilliseconds(minutes * 30),
            consent: consent);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
