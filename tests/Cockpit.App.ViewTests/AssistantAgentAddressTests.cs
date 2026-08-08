using System.Text.Json.Nodes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-632: the assistant is addressable on the agent line. It starts and coordinates the sessions on every desk, and
/// until this it had no place on any roster — so a session it spawned could never <c>notify</c> it back, and the only
/// route home was the assistant polling <c>list_sessions</c> or the operator relaying by hand. That is the same
/// delivery gap AC-119 measured for ordinary panes, with the assistant on the receiving end of it.
/// </summary>
/// <remarks>
/// This lives here rather than with the <c>AgentsMcpTools</c> unit tests for the reason
/// <see cref="WorkspaceAgentGatewayTests"/> gives: those substitute the gateway, so they can prove the tools ask it
/// the right question and never that it draws the roster correctly. The address is drawn by the gateway, against a
/// real <see cref="CockpitViewModel"/> holding a real assistant session — which is also what lets the last test here
/// run the whole line end to end (criterion 3) with no LLM in it: a real notify from a real pane, through the real
/// inbox, onto the result of a real tool call by the assistant.
/// </remarks>
[Collection("avalonia")]
public sealed class AssistantAgentAddressTests : IDisposable
{
    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"assistant-address-audit-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        McpRequestContext.Set(null);
        if (File.Exists(_auditPath))
        {
            File.Delete(_auditPath);
        }
    }

    /// <summary>
    /// Criterion 1. One assistant, and it is on every desk's roster — not because it sits on them (it sits on none,
    /// and <see cref="SessionWorkspacePlacement"/> still says so) but because it is the one thing every desk's
    /// sessions have in common. Two desks here rather than one: with a single desk this would pass on an
    /// implementation that put the assistant on whichever desk happened to be first.
    /// </summary>
    [Fact]
    public void EveryDesksRoster_CarriesTheAssistantsAddress_WhileOneIsRunning()
    {
        var (gateway, deskA, deskB) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit();
            var a = new SessionViewModel { WorkspaceId = "desk-a" };
            var b = new SessionViewModel { WorkspaceId = "desk-b" };
            cockpit.Sessions.Add(a);
            cockpit.Sessions.Add(b);
            cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

            return (_Gateway(cockpit, new WorkspaceAgentCoordinator()), a, b);
        });

        foreach (var caller in new[] { deskA, deskB })
        {
            var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(caller.PaneId).GetAwaiter().GetResult());

            Assert.NotNull(snapshot);
            Assert.Contains(snapshot!.Panes, pane => pane.PaneId == AssistantIdentity.PaneId);
            // And its own desk-mates are still what they were: the address is an addition, not a replacement.
            Assert.Contains(snapshot.Panes, pane => pane.PaneId == caller.PaneId);
            Assert.Equal(2, snapshot.Panes.Count);
        }
    }

    /// <summary>
    /// The other half, and the reason the row is conditional: an address with nobody behind it is mail delivered into
    /// an inbox nothing will ever open, reported to the sender as success. The assistant starts lazily and can be
    /// switched off entirely, so "no assistant" is an ordinary state and not an edge case.
    /// </summary>
    [Fact]
    public void WithNoAssistantRunning_NoRosterOffersItsAddress()
    {
        var (gateway, caller) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit();
            var session = new SessionViewModel { WorkspaceId = "desk-a" };
            cockpit.Sessions.Add(session);

            return (_Gateway(cockpit, new WorkspaceAgentCoordinator()), session);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(caller.PaneId).GetAwaiter().GetResult());

        Assert.NotNull(snapshot);
        Assert.DoesNotContain(snapshot!.Panes, pane => pane.PaneId == AssistantIdentity.PaneId);
    }

    /// <summary>
    /// Criterion 4, and the line this change deliberately does not cross. The assistant is now <em>addressable</em> on
    /// every desk; it is still <em>placed</em> on none, so asking the gateway which desk it is on answers nothing —
    /// which is what keeps every workspace-scoped tool (<c>list_agents</c>, <c>claim</c>, <c>notify</c>) scoped. Had
    /// this started returning a desk, the assistant would have been handed one desk's roster out of the several it
    /// manages, and AC-119's boundary would have become a coin toss rather than a rule.
    /// </summary>
    [Fact]
    public void TheAssistantsOwnPaneId_StillResolvesToNoDesk()
    {
        var gateway = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit();
            cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = "desk-a" });
            cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

            return _Gateway(cockpit, new WorkspaceAgentCoordinator());
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(AssistantIdentity.PaneId).GetAwaiter().GetResult());

        Assert.Null(snapshot);
    }

    /// <summary>
    /// An address is not a session a neighbour may start a turn on. Being on the roster puts the assistant within
    /// reach of an urgent <c>notify</c>, and a turn on the assistant is one spoken out loud to the operator — so the
    /// wake is refused, and refused <em>truthfully</em>: without this it lands on <c>PaneGone</c> ("no longer a live
    /// session") about a session that is live and did take the message, which is the sort of answer that makes a
    /// sender go looking for another route it does not need.
    /// </summary>
    [Fact]
    public void WakingTheAssistant_IsRefusedAsNotWakeable_RatherThanReportedGone()
    {
        var (gateway, caller) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit();
            var session = new SessionViewModel { WorkspaceId = "desk-a" };
            cockpit.Sessions.Add(session);
            cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

            return (_Gateway(cockpit, new WorkspaceAgentCoordinator()), session);
        });

        var outcome = Dispatcher.UIThread.Invoke(
            () => gateway.TryWakeAsync(caller.PaneId, AssistantIdentity.PaneId, "heads-up").GetAwaiter().GetResult());

        Assert.Equal(AgentWakeOutcome.NotWakeable, outcome);
    }

    /// <summary>
    /// Criterion 3, end to end and without a model in it: an agent on a desk notifies the assistant, and the assistant
    /// is handed the message on the result of the next tool call it makes — nobody polled, and nobody relayed.
    /// <para>
    /// Everything here is the real thing except the pane the assistant "calls" from, which is
    /// <see cref="McpRequestContext"/> — the same seam the transport stamps. The notify goes through the real
    /// <c>AgentsMcpTools</c> against the real gateway, so the address being on the roster is what makes it land
    /// rather than a substitute agreeing that it should.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnAgentNotifiesTheAssistant_AndItArrivesOnTheAssistantsNextToolResult()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        var inbox = new AgentMessageInbox();

        var (gateway, agentPaneId) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit();
            var session = new SessionViewModel { WorkspaceId = "desk-a" };
            cockpit.Sessions.Add(session);
            cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

            return (_Gateway(cockpit, coordinator), session.PaneId);
        });

        var tools = new AgentsMcpTools(
            gateway,
            coordinator,
            inbox,
            new AgentNotifyAuditLog(_auditPath, NullLogger<AgentNotifyAuditLog>.Instance),
            new AgentResourceClaims(),
            new AgentLineBudget(TimeProvider.System, TimeSpan.FromMinutes(1), 10_000, 10_000));

        // The agent's side: it addresses the assistant by the pane id list_agents showed it.
        McpRequestContext.Set(agentPaneId);
        var reply = JsonNode.Parse(await tools.NotifyAsync(AssistantIdentity.PaneId, "done", "AC-632 is on a branch and the PR is open."))!;

        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal(AssistantIdentity.PaneId, reply["deliveredTo"]!.GetValue<string>());

        // The assistant's side: its very next cockpit tool call, whatever it was for, comes back carrying the mail.
        McpRequestContext.Set(AssistantIdentity.PaneId);
        var result = McpInboxPiggyback.Attach(
            new CallToolResult { Content = [new TextContentBlock { Text = "the tool's own answer" }] },
            new AgentTurnInboxDelivery(inbox, coordinator),
            NullLogger.Instance);

        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("AC-632 is on a branch and the PR is open.", text, StringComparison.Ordinal);
        Assert.Contains(agentPaneId, text, StringComparison.Ordinal);
        // Read means read: the operator was never asked to pass anything on, and it does not arrive twice.
        Assert.NotNull(coordinator.LastInboxReadUtc(AssistantIdentity.PaneId));
    }

    private static WorkspaceAgentGateway _Gateway(CockpitViewModel cockpit, WorkspaceAgentCoordinator coordinator) =>
        new(cockpit, coordinator, NullLogger<WorkspaceAgentGateway>.Instance);

    // The smallest cockpit that can mint an assistant: CreateAssistantSession needs the session factory, which the
    // design-time constructor does not carry. Same shape as AssistantVoiceFanOutTests' helper, for the same reason.
    private static CockpitViewModel _Cockpit()
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminal = Substitute.For<ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcriptDisplay,
            sessionBehavior,
            layout,
            voice,
            terminal);
    }
}
