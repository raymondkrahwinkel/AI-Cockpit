using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Workspaces;
using Cockpit.Core;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
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
        string pluginId = "workflows",
        string? preApprovedBy = null) =>
        new("Workflow wants to run a command", action, new ConsentSource(paneId, pluginId, "Workflows"), scope, risk, allowRemember, preApprovedBy);

    /// <summary>
    /// AC-1348: a plugin's own policy (a daemon or cluster consent mode) can name itself as the reason a request
    /// never needed a card, independent of AC-575's operator-configured bypass below. The audit still gets its
    /// own line — Bypassed, not Approved — so the skip stays visible even though nobody clicked anything.
    /// </summary>

    /// <summary>
    /// The core safety property: a dangerous action is asked every single time. Even when the operator ticked
    /// "remember" on the first one, the second identical request still stops and asks — otherwise one approval
    /// becomes a standing permission a prompt-injected call can reuse.
    /// </summary>
    [Fact]
    public async Task RequestConsentAsync_DangerousRequest_IsNeverRememberedAcrossCalls()
    {
        var auditEntries = new List<ConsentAuditEntry>();
        _audit.RecordAsync(Arg.Do<ConsentAuditEntry>(auditEntries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
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

        // Category A: only a current, device-bound, one-use capability reaches the same atomic consent callback.
        var mobileBroker = CreateBroker();
        var actions = new OperatorActionService(NullLogger<OperatorActionService>.Instance, mobileBroker);
        using var services = new ServiceCollection().AddSingleton(actions).BuildServiceProvider();
        PluginBackendHost Host(string pluginId) => new(pluginId, pluginId, services, Substitute.For<IPluginStorage>(),
            Substitute.For<ICockpitSessionObserver>(), Substitute.For<ICockpitActions>(), new PluginDiagnostics());
        Assert.Null(Host("untrusted").OperatorTransport);
        Assert.Same(actions, Host("untrusted").OperatorActions);
        var transport = Assert.IsAssignableFrom<IOperatorActionTransport>(Host("depot").OperatorTransport);
        using var phone = transport.OpenChannel();
        using var stranger = transport.OpenChannel();
        var pending = mobileBroker.RequestConsentAsync(request);
        var offered = Assert.Single(phone.Snapshot);
        Assert.Equal(request.Title, offered.Title);
        Assert.Equal(request.Risk.ToString(), offered.Risk);
        Assert.Equal(request.Source.PluginId, offered.SourcePlugin);
        Assert.False((await stranger.RespondAsync(offered.Id, offered.Revision, offered.Capability, "approve")).Ok);
        Assert.False((await phone.RespondAsync(offered.Id, "obsolete", offered.Capability, "approve")).Ok);
        Assert.False((await phone.RespondAsync(offered.Id, offered.Revision, offered.Capability, "akkoord")).Ok);
        Assert.False(pending.IsCompleted);
        try
        {
            McpRequestContext.Set("cockpit-assistant");
            Assert.False((await phone.RespondAsync(offered.Id, offered.Revision, offered.Capability, "approve")).Ok);
        }
        finally
        {
            McpRequestContext.Set(null);
        }

        Assert.True((await phone.RespondAsync(offered.Id, offered.Revision, offered.Capability, "approve")).Ok);
        Assert.True((await pending).IsApproved);
        Assert.Equal(OperatorDecisionOrigin.PhoneOperator, auditEntries[^1].Origin);
        Assert.False((await phone.RespondAsync(offered.Id, offered.Revision, offered.Capability, "approve")).Ok);

        var next = mobileBroker.RequestConsentAsync(request);
        var desktopFirst = Assert.Single(phone.Snapshot);
        mobileBroker.Respond(Guid.Parse(desktopFirst.Id), ConsentOutcome.Denied, false);
        Assert.False((await phone.RespondAsync(desktopFirst.Id, desktopFirst.Revision, desktopFirst.Capability, "approve")).Ok);
        Assert.False((await next).IsApproved);
        Assert.Equal(OperatorDecisionOrigin.Desktop, auditEntries[^1].Origin);

        var disconnected = mobileBroker.RequestConsentAsync(request);
        var withdrawn = Assert.Single(phone.Snapshot);
        phone.Dispose();
        Assert.False((await phone.RespondAsync(withdrawn.Id, withdrawn.Revision, withdrawn.Capability, "approve")).Ok);
        mobileBroker.Respond(Guid.Parse(withdrawn.Id), ConsentOutcome.Denied, false);
        Assert.False((await disconnected).IsApproved);

        var attempts = 0;
        var waiting = true;
        using var retryable = actions.Register(new OperatorAction("retryable", "r1", "consent", "", "test callback", ["approve"]),
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("callback failed before deciding");
                }

                waiting = attempts < 3;
                return Task.FromResult(!waiting);
            }, () => waiting);
        var beforeFailure = Assert.Single(stranger.Snapshot);
        Assert.False((await stranger.RespondAsync(beforeFailure.Id, beforeFailure.Revision, beforeFailure.Capability, "approve")).Ok);
        var afterThrow = Assert.Single(stranger.Snapshot);
        Assert.NotEqual(beforeFailure.Capability, afterThrow.Capability);
        Assert.False((await stranger.RespondAsync(beforeFailure.Id, beforeFailure.Revision, beforeFailure.Capability, "approve")).Ok);
        Assert.False((await stranger.RespondAsync(afterThrow.Id, afterThrow.Revision, afterThrow.Capability, "approve")).Ok);
        var afterRefusal = Assert.Single(stranger.Snapshot);
        Assert.NotEqual(afterThrow.Capability, afterRefusal.Capability);
        Assert.True((await stranger.RespondAsync(afterRefusal.Id, afterRefusal.Revision, afterRefusal.Capability, "approve")).Ok);
        Assert.Equal(3, attempts);
        Assert.Empty(stranger.Snapshot);

        // Category A: lifecycle authority stays on the authenticated channel, including retries after reconnect.
        var launcher = Substitute.For<ISessionLauncher>();
        var registry = Substitute.For<ISessionRegistry>();
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync().Returns(new[] { new SessionProfile("sdk", new ClaudeConfig(string.Empty)) });
        launcher.Workspaces.Returns(new WorkspaceSettings { Workspaces = [new Workspace("desk", "Desk", WorkspaceType.Sessions)
        {
            Panes = [new WorkspacePane("agent", PaneKind.AiSession), new WorkspacePane("shell", PaneKind.Terminal)],
        }] });
        launcher.RunExclusiveAsync(Arg.Any<Func<bool>>()).Returns(call => Task.FromResult(call.Arg<Func<bool>>()()));
        launcher.RunExclusiveAsync(Arg.Any<Func<OperatorSessionChoice[]>>()).Returns(call => Task.FromResult(call.Arg<Func<OperatorSessionChoice[]>>()()));
        launcher.StartSessionAsync(Arg.Any<SessionLaunchRequest>()).Returns(new LaunchedSession("started", "SDK", null));
        var agent = Substitute.For<ISessionHandle>();
        registry.Find("agent").Returns(agent);
        var shell = Substitute.For<ISessionHandle>();
        shell.IsTerminal.Returns(true);
        registry.Find("shell").Returns(shell);
        var lifecycle = new OperatorActionService(NullLogger<OperatorActionService>.Instance, sessions: registry, launcher: launcher, profiles: profiles);
        using var lifecycleServices = new ServiceCollection().AddSingleton(lifecycle).BuildServiceProvider();
        var lifecycleHost = new PluginBackendHost("depot", "Depot", lifecycleServices, Substitute.For<IPluginStorage>(),
            Substitute.For<ICockpitSessionObserver>(), Substitute.For<ICockpitActions>(), new PluginDiagnostics());
        var lifecycleTransport = Assert.IsAssignableFrom<IOperatorActionTransport>(lifecycleHost.OperatorTransport);
        using var owner = lifecycleTransport.OpenChannel("paired-phone");
        using var other = lifecycleTransport.OpenChannel("other-phone");
        var catalog = await owner.SessionCatalogAsync();
        Assert.False((await owner.StartSessionAsync("old-host", catalog.Capability, "expired-epoch", "desk", "sdk")).Ok);
        Assert.False((await other.StartSessionAsync("request", catalog.Capability, catalog.Epoch, "desk", "sdk")).Ok);
        Assert.False((await owner.StartSessionAsync("bad-desk", catalog.Capability, catalog.Epoch, "unknown", "sdk")).Ok);
        Assert.False((await owner.StartSessionAsync("bad-profile", catalog.Capability, catalog.Epoch, "desk", "unknown")).Ok);
        try
        {
            McpRequestContext.Set("cockpit-assistant");
            Assert.False((await owner.StartSessionAsync("mcp", catalog.Capability, catalog.Epoch, "desk", "sdk")).Ok);
            Assert.False((await owner.StopSessionAsync(catalog.Capability, "agent")).Ok);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
        Assert.True((await owner.StartSessionAsync("request", catalog.Capability, catalog.Epoch, "desk", "sdk")).Ok);
        owner.Dispose();
        Assert.False((await owner.StopSessionAsync(catalog.Capability, "agent")).Ok);
        using var reconnected = lifecycleTransport.OpenChannel("paired-phone");
        var fresh = await reconnected.SessionCatalogAsync();
        Assert.False((await reconnected.StartSessionAsync("request", catalog.Capability, catalog.Epoch, "desk", "sdk")).Ok);
        Assert.Equal("started", (await reconnected.StartSessionAsync("request", fresh.Capability, fresh.Epoch, "desk", "sdk")).PaneId);
        Assert.False((await reconnected.StartSessionAsync("request", fresh.Capability, fresh.Epoch, "different", "sdk")).Ok);
        await launcher.Received(1).StartSessionAsync(Arg.Is<SessionLaunchRequest>(start => start.WorkspaceId == "desk" && start.Profile.Label == "sdk" && start.Kind == null));
        Assert.False((await reconnected.StopSessionAsync(fresh.Capability, "shell")).Ok);
        Assert.False((await reconnected.StopSessionAsync(fresh.Capability, "cockpit-assistant")).Ok);
        Assert.False((await reconnected.StopSessionAsync(fresh.Capability, "gone")).Ok);
        Assert.True((await reconnected.StopSessionAsync(fresh.Capability, "agent")).Ok);
        await launcher.Received(1).StopSessionAsync("agent");
        await launcher.DidNotReceive().StopSessionAsync("shell");
    }

    /// <summary>The low-risk counterpart: once remembered, the second identical request is not asked again.</summary>

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

    /// <summary>A remembered scope only skips the class it was granted for: a dangerous request of the same scope still asks.</summary>

    /// <summary>With nothing listening to show a prompt, the gate denies rather than blocking forever or passing silently.</summary>

    /// <summary>A request whose caller token is cancelled while it waits is denied, and its prompt is taken down.</summary>

    /// <summary>A denial is never remembered, even with the box ticked — so the next request is asked again.</summary>

    /// <summary>
    /// L2c (contract), AC-1714: a card with choices hands back what the operator picked, and only that. A pick the
    /// card did not offer or enable, no pick, a deadline, and a plugin pre-approving it are all a plain denial.
    /// </summary>
    [Fact]
    public async Task RequestConsentAsync_WithChoices_ReturnsOnlyAnOfferedPick_AndFailsClosedOtherwise()
    {
        var entries = new List<ConsentAuditEntry>();
        _audit.RecordAsync(Arg.Do<ConsentAuditEntry>(entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var broker = CreateBroker();
        var answers = new Queue<IReadOnlyDictionary<string, IReadOnlyList<string>>?>();
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            if (answers.TryDequeue(out var pick))
            {
                broker.Respond(prompt.Id, ConsentOutcome.Approved, remember: true, pick);
            }
        };
        var request = Request(ConsentRisk.Dangerous, allowRemember: true) with
        {
            Choices =
            [
                new ConsentChoice("session", "With", [new ConsentOption("pane-a", "A"), new ConsentOption("pane-b", "B")]),
                new ConsentChoice("mode", "May", [new ConsentOption("watch", "Watch"), new ConsentOption("control", "Control", Enabled: false)], AllowMultiple: true),
            ],
        };

        answers.Enqueue(new Dictionary<string, IReadOnlyList<string>> { ["session"] = ["pane-b"], ["mode"] = ["watch"] });
        var approved = await broker.RequestConsentAsync(request);
        answers.Enqueue(new Dictionary<string, IReadOnlyList<string>> { ["session"] = ["pane-b"], ["mode"] = ["watch", "control"] });
        var disabledPick = await broker.RequestConsentAsync(request);
        answers.Enqueue(new Dictionary<string, IReadOnlyList<string>> { ["session"] = ["pane-a", "pane-b"], ["mode"] = ["watch"] });
        var twoSessions = await broker.RequestConsentAsync(request);
        answers.Enqueue(null);
        var noPick = await broker.RequestConsentAsync(request);
        using var deadline = new CancellationTokenSource();
        var timedOut = broker.RequestConsentAsync(request, deadline.Token);
        await deadline.CancelAsync();
        var preApproved = await broker.RequestConsentAsync(request with { PreApprovedBy = "plugin policy" });

        Assert.True(approved.IsApproved);
        Assert.Equal(["pane-b"], approved.Chosen!["session"]);
        Assert.Equal(["watch"], approved.Chosen["mode"]);
        Assert.Equal("session=pane-b; mode=watch", entries[0].Chosen);
        Assert.All([disabledPick, twoSessions, noPick, await timedOut, preApproved], decision =>
        {
            Assert.False(decision.IsApproved);
            Assert.Null(decision.Chosen);
        });
        Assert.Equal(5, prompts.Count);
        Assert.All(prompts, prompt => Assert.False(prompt.CanRemember));
    }

    /// <summary>Every decision reaches the audit trail, carrying the ground-truth action rather than any framing.</summary>

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

            Assert.Equal("plugin:Terminal MCP/Workflows", policy.Asked[0].SourceKey);
            Assert.Equal(ConsentSourceCatalog.TerminalMcp, policy.Asked[1].SourceKey);
            Assert.NotEqual(policy.Asked[0].SourceKey, policy.Asked[1].SourceKey);
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
    public async Task RequestConsentAsync_AnElevatedCommand_ShowsTheCardWithoutEverAskingTheBypass()
    {
        // AC-1335: the policy would say yes to anything here ("allow all"), yet the card still comes — and the
        // policy is not even consulted, so no list an operator can tick makes this a blind UAC prompt.
        var policy = new StubPolicy(answer: true);
        var broker = new ConsentService(_audit, policy);
        var prompts = _RecordPrompts(broker);

        try
        {
            McpRequestContext.Set("cockpit-assistant");
            var request = new ConsentRequest(
                "A session wants to run a command as administrator",
                "powershell.exe -NoProfile -NonInteractive -Command \"& { wsl --install } *> 'C:\\t\\out.txt'\"",
                new ConsentSource("cockpit-assistant", null, ConsentSourceCatalog.ElevatedCommand),
                "session.elevated-command",
                ConsentRisk.Dangerous);
            var decision = await broker.RequestConsentAsync(request);

            Assert.Single(prompts);
            Assert.False(decision.Bypassed);
            Assert.Empty(policy.Asked);
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
