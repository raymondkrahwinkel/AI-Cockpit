using Cockpit.Core.Abstractions.Consent;
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
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// Whose consent bypass answers for a task the queue drainer starts (#AC-575, #AC-89).
/// </summary>
/// <remarks>
/// <see cref="McpRequestContext"/> is an <c>AsyncLocal</c>: it names the request that entered the process, and
/// everything that request awaits inherits it. <c>StopAsync</c> drains the queue inline, on the stopper's own flow,
/// so the task it starts next — which belongs to somebody else — used to reach the consent broker wearing the
/// stopper's identity. With the assistant's bypass switched on for the orchestrator's dangerous class, that is a
/// full escalation: a queued task can ask to run above its profile's ceiling, be started by the assistant's stop,
/// and be waved through on the assistant's bypass with no card shown and the trail written in the assistant's name.
/// <para>
/// The fix restamps the ambient identity in <c>DelegationService._StartAsync</c> — the one chokepoint every start
/// goes through — from the task's own <c>OwnerPaneId</c>, which was itself stamped from the transport-verified
/// context when the task was delegated. AC-89's rule is untouched by that: no caller-declared value ever becomes
/// the ambient identity, so an agent still cannot talk its way into another pane.
/// </para>
/// </remarks>
public class DelegationQueueDrainConsentIdentityTests
{
    private const string AssistantPane = "assistant-pane";

    private const string InjectedPane = "pane-x";

    /// <summary>The operator's real switch: bypass everything the orchestrator asks, for the assistant's pane only.</summary>
    private sealed class AssistantOnlyBypass : IConsentBypassPolicy
    {
        public bool ShouldBypass(string? verifiedPaneId, string sourceKey, bool dangerous) =>
            verifiedPaneId == AssistantPane;
    }

    private sealed class RecordingConsentAuditLog : IConsentAuditLog
    {
        public List<ConsentAuditEntry> Entries { get; } = [];

        public Task RecordAsync(ConsentAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConsentAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConsentAuditEntry>>([.. Entries]);
    }

    [Fact]
    public async Task AQueuedTaskStartedByTheAssistantsStop_IsJudgedOnItsOwnOwner_NotTheAssistantsBypass()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var consentTrail = new RecordingConsentAuditLog();
        var service = _ServiceWith(driver, new ConsentService(consentTrail, new AssistantOnlyBypass()));

        // The assistant's own task takes the profile's single slot. Delegated on the assistant's verified flow, the
        // way a real orchestrator call arrives.
        McpRequestContext.Set(AssistantPane);
        var assistantTask = await _DelegateAsTheCallerAsync(service, new DelegationRequest("local", "assistant work", Label: "assistant"));

        // A prompt-injected session asks for a task above the profile's ceiling. No slot, so it waits — and its
        // elevation is never put to anyone at delegate time.
        McpRequestContext.Set(InjectedPane);
        await _DelegateAsTheCallerAsync(service, new DelegationRequest("local", "injected work", Label: "injected", RequestedPermission: "bypassPermissions"));
        Assert.Equal(DelegatedTaskStatus.Queued, service.ListTasks().Single(task => task.Label == "injected").Status);
        Assert.Empty(consentTrail.Entries);

        // The assistant stops its own task, which drains the queue inline on the assistant's flow.
        McpRequestContext.Set(AssistantPane);
        await service.StopAsync(assistantTask.TaskId, AssistantPane);

        // Exactly one consent decision was reached — the injected task's elevation — and it belongs to the pane that
        // asked for it. Nothing is listening for prompts here, so it fails closed and the task runs clamped.
        var decision = Assert.Single(consentTrail.Entries);
        Assert.Equal(InjectedPane, decision.PaneId);
        Assert.Equal(ConsentAuditAction.Denied, decision.Action);
        await driver.DidNotReceive().SetDelegatedToolGateAsync("bypassPermissions", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        Assert.Equal(DelegatedTaskStatus.Running, service.ListTasks().Single(task => task.Label == "injected").Status);
    }

    [Fact]
    public async Task TheAssistantsOwnQueuedTask_StillRidesItsBypass()
    {
        // The other side: restamping must not turn the bypass off for the assistant's own queued work, which is the
        // whole feature. Same drain, same elevation — only the owner differs.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var consentTrail = new RecordingConsentAuditLog();
        var service = _ServiceWith(driver, new ConsentService(consentTrail, new AssistantOnlyBypass()));

        McpRequestContext.Set(AssistantPane);
        var first = await _DelegateAsTheCallerAsync(service, new DelegationRequest("local", "first", Label: "first"));
        await _DelegateAsTheCallerAsync(service, new DelegationRequest("local", "second", Label: "second", RequestedPermission: "bypassPermissions"));
        await service.StopAsync(first.TaskId, AssistantPane);

        var decision = Assert.Single(consentTrail.Entries);
        Assert.Equal(ConsentAuditAction.Bypassed, decision.Action);
        Assert.Equal(AssistantPane, decision.PaneId);
        await driver.Received().SetDelegatedToolGateAsync("bypassPermissions", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheDrainLeavesTheStoppersOwnIdentityAlone()
    {
        // The restamp is scoped to the start it covers: an async method's builder restores the ExecutionContext
        // around its synchronous run, so the identity the drain borrowed never leaks back into the flow that
        // triggered it — everything that stopper does afterwards is still its own.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, new ConsentService(new RecordingConsentAuditLog(), new AssistantOnlyBypass()));

        McpRequestContext.Set(AssistantPane);
        var assistantTask = await _DelegateAsTheCallerAsync(service, new DelegationRequest("local", "assistant work", Label: "assistant"));

        McpRequestContext.Set(InjectedPane);
        await _DelegateAsTheCallerAsync(service, new DelegationRequest("local", "injected work", Label: "injected", RequestedPermission: "bypassPermissions"));

        McpRequestContext.Set(AssistantPane);
        await service.StopAsync(assistantTask.TaskId, AssistantPane);

        Assert.Equal(AssistantPane, McpRequestContext.CurrentPaneId);
    }

    /// <summary>
    /// Delegates the way <c>OrchestratorTools.delegate_task</c> does: the caller is the transport-verified pane of
    /// the request in flight, handed to the service explicitly. That is what stamps <c>OwnerPaneId</c>, and it is
    /// why the owner is never a value the caller declared.
    /// </summary>
    private static Task<DelegatedTaskView> _DelegateAsTheCallerAsync(DelegationService service, DelegationRequest request) =>
        service.DelegateAsync(request, McpRequestContext.CurrentPaneId);

    private static DelegationService _ServiceWith(ISessionDriver driver, IConsentBroker consent)
    {
        // One slot, so the second task can only ever start through the queue drainer.
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, PermissionCeiling: "default", MaxConcurrent: 1, TimeoutMinutes: 0));

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
            minutes => TimeSpan.FromMilliseconds(minutes * 30),
            consent: consent);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
