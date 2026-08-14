using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;
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

        Assert.True(first.IsApproved);
        Assert.True(second.IsApproved);
        Assert.Equal(2, System.Linq.Enumerable.Count(prompts));
        Assert.All(prompts, prompt => Assert.True(!prompt.CanRemember, "the remember option is never offered for the dangerous class"));
        Assert.False(first.Remembered);
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

        Assert.Single(prompts);
        Assert.True(first.Remembered);
        Assert.Equal(new ConsentDecision(ConsentOutcome.Approved, Remembered: true), second);
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

        Assert.Equal(2, System.Linq.Enumerable.Count(prompts));
        Assert.Equal("GET https://evil.example/exfil", prompts[1].Request.Action);
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

        Assert.Equal(2, System.Linq.Enumerable.Count(prompts));
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

        Assert.Equal(2, System.Linq.Enumerable.Count(prompts));
    }

    /// <summary>With nothing listening to show a prompt, the gate denies rather than blocking forever or passing silently.</summary>
    [Fact]
    public async Task RequestConsentAsync_NoUiListening_FailsClosed()
    {
        var broker = CreateBroker();

        var decision = await broker.RequestConsentAsync(Request(ConsentRisk.Dangerous));

        Assert.Equal(ConsentDecision.Denied, decision);
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

        Assert.Equal(ConsentDecision.Denied, decision);
        Assert.Equal(opened, Assert.Single(closed));
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

        Assert.False(first.IsApproved);
        Assert.Equal(2, System.Linq.Enumerable.Count(prompts));
    }

    [Fact]
    public void Respond_UnknownId_IsIgnored()
    {
        var broker = CreateBroker();

        var act = () => broker.Respond(Guid.NewGuid(), ConsentOutcome.Approved, remember: false);

        act();
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

        Assert.Single(entries);
        Assert.Equal(ConsentAuditAction.Approved, entries[0].Action);
        Assert.Equal("rm -rf /tmp/x", entries[0].ActionText);
        Assert.Equal("workflows", entries[0].PluginId);
        Assert.Equal("workflow.command", entries[0].Scope);
    }

    /// <summary>
    /// The decision resolves only once the audit line is flushed, not before — so a caller cannot act on an
    /// approval the append-only trail has not yet recorded (code review, C4).
    /// </summary>
    [Fact]
    public async Task RequestConsentAsync_Approve_ResolvesOnlyAfterTheAuditIsWritten()
    {
        var auditGate = new TaskCompletionSource();
        _audit.RecordAsync(Arg.Any<ConsentAuditEntry>()).Returns(auditGate.Task);
        var broker = CreateBroker();
        broker.PromptOpened += (_, prompt) => broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: false);

        var decision = broker.RequestConsentAsync(Request(ConsentRisk.Dangerous));

        Assert.False(decision.IsCompleted, "the decision must wait for the audit line to be flushed");
        auditGate.SetResult();
        Assert.True((await decision).IsApproved);
    }

    /// <summary>A fail-closed denial is logged too — the "nobody asked but it was refused" line you want afterwards.</summary>
    [Fact]
    public async Task RequestConsentAsync_FailClosed_WritesADeniedAuditEntry()
    {
        var entries = new List<ConsentAuditEntry>();
        _ = _audit.RecordAsync(Arg.Do<ConsentAuditEntry>(entries.Add));
        var broker = CreateBroker();

        await broker.RequestConsentAsync(Request(ConsentRisk.Dangerous));

        Assert.Single(entries);
        Assert.Equal(ConsentAuditAction.Denied, entries[0].Action);
    }

    /// <summary>
    /// AC-89: the remember scope keys on the transport-verified session, not the id the agent declares. Another pane's
    /// agent that forges a remembered pane's id in the request is re-prompted anyway, because the broker overrides the
    /// declared id with the pane the request actually came from (the ambient <c>McpRequestContext</c>).
    /// </summary>
    [Fact]
    public async Task RequestConsentAsync_ScopesRememberOnTheVerifiedSession_NotTheAgentDeclaredId()
    {
        var broker = CreateBroker();
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: true);
        };

        // The agent always declares the same id ("P1") in the request — the exploit is a second pane claiming it.
        var request = Request(ConsentRisk.LowRisk, allowRemember: true, paneId: "P1", scope: "k8s.namespace:prod:kube-system");
        try
        {
            McpRequestContext.Set("P1");
            await broker.RequestConsentAsync(request);            // real P1: approved and remembered under P1
            McpRequestContext.Set("P1");
            await broker.RequestConsentAsync(request);            // real P1 again: rides its own remembered approval

            McpRequestContext.Set("P2");
            var forged = await broker.RequestConsentAsync(request); // P2 forging session:"P1" — must be asked afresh

            Assert.Equal(2, System.Linq.Enumerable.Count(prompts));
            Assert.True(forged.IsApproved, "it was approved — but only after asking, not silently on P1's remember");
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    /// <summary>Off the verified path (the in-process tool loop, UI-side consent), the identity is null and the request is used as declared — the previous behaviour.</summary>
    [Fact]
    public async Task RequestConsentAsync_WithNoVerifiedIdentity_UsesTheDeclaredId()
    {
        var broker = CreateBroker();
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: true);
        };

        var request = Request(ConsentRisk.LowRisk, allowRemember: true, paneId: "P1", scope: "k8s.namespace:prod:kube-system");
        McpRequestContext.Set(null);
        await broker.RequestConsentAsync(request);
        await broker.RequestConsentAsync(request);

        Assert.Single(prompts);
    }

    // ── AC-575: the assistant's consent bypass ────────────────────────────────────────────────────────────────
    //
    // The gate opens a hole in a security gate on purpose, so what these hold shut is the ways the hole widens:
    // a forged pane id, a dangerous action riding the everyday switch, a source nobody switched on, and a bypass
    // quietly turning into a remembered approval that outlives it.

    /// <summary>A policy that answers from a fixed set, and records exactly what it was asked — the questions matter as much as the answers.</summary>
    private sealed class StubPolicy(bool answer = true) : IConsentBypassPolicy
    {
        public List<(string? VerifiedPaneId, string SourceKey, bool Dangerous)> Asked { get; } = [];

        public bool Answer { get; set; } = answer;

        public bool ShouldBypass(string? verifiedPaneId, string sourceKey, bool dangerous)
        {
            Asked.Add((verifiedPaneId, sourceKey, dangerous));
            return Answer;
        }
    }

    private List<ConsentPrompt> _RecordPrompts(ConsentService broker)
    {
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: false);
        };
        return prompts;
    }

    [Fact]
    public async Task RequestConsentAsync_WithABypassingPolicy_SkipsThePrompt_AndLogsBypassedRatherThanApproved()
    {
        var entries = new List<ConsentAuditEntry>();
        _audit.RecordAsync(Arg.Do<ConsentAuditEntry>(entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var policy = new StubPolicy();
        var broker = new ConsentService(_audit, policy);
        var prompts = _RecordPrompts(broker);

        try
        {
            McpRequestContext.Set("cockpit-assistant");
            var decision = await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk));

            Assert.True(decision.IsApproved);
            Assert.True(decision.Bypassed, "AC-759: a caller reading only IsApproved cannot tell this from a card the operator actually clicked.");
            Assert.Empty(prompts);

            // Its own audit value, not Approved-with-a-flag: the trail has to distinguish an approval the operator
            // gave from one they had clicked away in advance.
            var entry = Assert.Single(entries);
            Assert.Equal(ConsentAuditAction.Bypassed, entry.Action);
            Assert.Equal("workflows", entry.PluginId);
            Assert.Equal("workflow.command", entry.Scope);
            Assert.Equal("cockpit-assistant", entry.PaneId);
            Assert.False(entry.Remembered);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task RequestConsentAsync_AskingTheBypass_PassesTheVerifiedPane_AndTheHostStampedSource()
    {
        // The three facts the decision may rest on, and no others. The source key is the host-stamped plugin id
        // under its own prefix, or the label — never the scope or the action, which are text an agent influences;
        // keying on those is how a bypass for one thing becomes a bypass for another.
        var policy = new StubPolicy(answer: false);
        var broker = new ConsentService(_audit, policy);
        _RecordPrompts(broker);

        try
        {
            McpRequestContext.Set("cockpit-assistant");
            await broker.RequestConsentAsync(Request(ConsentRisk.Dangerous, paneId: "pane-the-agent-typed"));
            await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk, pluginId: null!));

            Assert.Equal(("cockpit-assistant", "plugin:workflows", true), policy.Asked[0]);
            Assert.Equal(("cockpit-assistant", "Workflows", false), policy.Asked[1]);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task RequestConsentAsync_APluginWhoseIdIsAHostLabel_DoesNotShareThatSourcesSwitch()
    {
        // A plugin declares its own manifest id, and the host stamps it faithfully — so a plugin published as
        // "Terminal MCP" would, on a flat key space, be handed the row the operator ticked for the cockpit's own
        // terminal gate. The two live in separate spaces: the host's label is bare, a plugin's id is prefixed.
        var policy = new StubPolicy(answer: false);
        var broker = new ConsentService(_audit, policy);
        _RecordPrompts(broker);

        try
        {
            McpRequestContext.Set("cockpit-assistant");
            await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk, pluginId: ConsentSourceCatalog.TerminalMcp));
            await broker.RequestConsentAsync(
                new ConsentRequest("The terminal wants to run a command", "ls", new ConsentSource("pane-1", null, ConsentSourceCatalog.TerminalMcp), "terminal.run", ConsentRisk.LowRisk));

            Assert.Equal("plugin:Terminal MCP", policy.Asked[0].SourceKey);
            Assert.Equal(ConsentSourceCatalog.TerminalMcp, policy.Asked[1].SourceKey);
            Assert.NotEqual(policy.Asked[0].SourceKey, policy.Asked[1].SourceKey);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task RequestConsentAsync_ARiskClassThisBuildDoesNotKnow_IsOfferedOnlyToTheDangerousSwitch()
    {
        // Polarity, and it has to fail closed. A risk value added later — or one an older host reads off a newer
        // plugin — is not low risk, so it must arrive at the policy as dangerous and need the deliberate second
        // switch. Asking "is it Dangerous?" would let it through on the everyday one instead.
        var policy = new StubPolicy(answer: false);
        var broker = new ConsentService(_audit, policy);
        _RecordPrompts(broker);

        try
        {
            McpRequestContext.Set("cockpit-assistant");
            await broker.RequestConsentAsync(Request((ConsentRisk)99));

            Assert.True(Assert.Single(policy.Asked).Dangerous);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task RequestConsentAsync_WithAForgedAssistantPaneAndNoVerifiedIdentity_StillShowsTheCard()
    {
        // The attack the placement of this check exists to stop. The agent writes the assistant's pane id into its
        // own Source.PaneId; off the verified transport there is nothing to override it with, so the request looks
        // like the assistant's to anything reading the request. The bypass is not offered that request at all —
        // it is never even asked, which is stronger than being asked and saying no.
        var policy = new StubPolicy();
        var broker = new ConsentService(_audit, policy);
        var prompts = _RecordPrompts(broker);

        McpRequestContext.Set(null);
        var decision = await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk, paneId: "cockpit-assistant"));

        Assert.Single(prompts);
        Assert.True(decision.IsApproved, "approved — but by the operator, on the card, not by the bypass");
        Assert.Empty(policy.Asked);
    }

    [Fact]
    public async Task RequestConsentAsync_WhenTheBypassSaysNo_ShowsTheCardExactlyAsBefore()
    {
        var policy = new StubPolicy(answer: false);
        var broker = new ConsentService(_audit, policy);
        var prompts = _RecordPrompts(broker);

        try
        {
            McpRequestContext.Set("cockpit-assistant");
            await broker.RequestConsentAsync(Request(ConsentRisk.Dangerous));

            Assert.Single(prompts);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task RequestConsentAsync_WithNoPolicyRegistered_NeverBypasses()
    {
        // Fail-closed by construction: the broker takes the policy as an optional dependency, so a graph that has
        // none — the design-time one, every test that predates this ticket — cannot bypass anything.
        var broker = CreateBroker();
        var prompts = _RecordPrompts(broker);

        try
        {
            McpRequestContext.Set("cockpit-assistant");
            await broker.RequestConsentAsync(Request(ConsentRisk.LowRisk));

            Assert.Single(prompts);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task RequestConsentAsync_ABypassedRequest_DoesNotBecomeARememberedApproval()
    {
        // Why the check sits before the remember set and not after it. A bypass is the stronger statement of the
        // two and must not leave the weaker one behind: switching the source off has to take the exemption with
        // it, rather than finding the same action now silently approved by a remember the operator never gave.
        var policy = new StubPolicy();
        var broker = new ConsentService(_audit, policy);
        var prompts = _RecordPrompts(broker);

        try
        {
            McpRequestContext.Set("cockpit-assistant");
            var request = Request(ConsentRisk.LowRisk, allowRemember: true);
            await broker.RequestConsentAsync(request);
            Assert.Empty(prompts);

            policy.Answer = false;                       // the operator unticks the source
            await broker.RequestConsentAsync(request);

            Assert.Single(prompts);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }
}
