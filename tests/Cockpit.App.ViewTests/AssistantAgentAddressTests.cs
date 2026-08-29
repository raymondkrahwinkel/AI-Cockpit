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
/// AC-632: the assistant is addressable on the agent line, so a session it spawned can <c>notify</c> it back.
/// Here rather than with the <c>AgentsMcpTools</c> unit tests, which substitute the gateway that draws the roster.
/// </summary>
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
    /// Criterion 1: one assistant, on every desk's roster. Two desks, or this passes on an implementation that put
    /// it on whichever desk came first.
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

            return (_Gateway(cockpit), a, b);
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
    /// Why the row is conditional: an address with nobody behind it is mail nothing will ever open, reported to the
    /// sender as success. The assistant starts lazily and can be off, so this is an ordinary state.
    /// </summary>
    [Fact]
    public void WithNoAssistantRunning_NoRosterOffersItsAddress()
    {
        var (gateway, caller) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit();
            var session = new SessionViewModel { WorkspaceId = "desk-a" };
            cockpit.Sessions.Add(session);

            return (_Gateway(cockpit), session);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(caller.PaneId).GetAwaiter().GetResult());

        Assert.NotNull(snapshot);
        Assert.DoesNotContain(snapshot!.Panes, pane => pane.PaneId == AssistantIdentity.PaneId);
    }

    /// <summary>
    /// Criterion 4: addressable on every desk, placed on none. A desk here would hand the assistant one roster out
    /// of the several it manages, making AC-119's boundary a coin toss.
    /// </summary>
    [Fact]
    public void TheAssistantsOwnPaneId_StillResolvesToNoDesk()
    {
        var gateway = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit();
            cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = "desk-a" });
            cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

            return _Gateway(cockpit);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(AssistantIdentity.PaneId).GetAwaiter().GetResult());

        Assert.Null(snapshot);
    }

    /// <summary>
    /// <c>cockpit-session</c> is mounted for every session including this one, and <c>set_status</c> still refuses the
    /// assistant: the statusline is written onto a session found in the grid's own list, which the assistant is
    /// deliberately not in. Asserted because the capability map now tells the assistant so, and a map that says
    /// "this one is not for you" about a tool that quietly starts working is worse than one that never mentioned it.
    /// </summary>
    [Fact]
    public void TheAssistantsStatusline_CannotBeSet_BecauseItIsNotInTheListThatWritesThem()
    {
        var cockpit = Dispatcher.UIThread.Invoke(() =>
        {
            var viewModel = _Cockpit();
            viewModel.CreateAssistantSession(AssistantIdentity.PaneId);
            return viewModel;
        });

        Assert.False(Dispatcher.UIThread.Invoke(() => cockpit.SetSessionStatusline(AssistantIdentity.PaneId, "AC-635")));
    }

    /// <summary>
    /// AC-656: the assistant is resolved through the same target lookup as any pane now (`AssistantPane`, since
    /// `AllSessions` still never carries it) and runs the same gate — no special-cased refusal left for it. Asserted
    /// against an unstarted assistant session on purpose: before this ticket every target hit the same
    /// <c>NotWakeable</c> short-circuit regardless of state, so a <em>state-dependent</em> outcome
    /// (<c>CannotTakeATurn</c>, exactly what an unstarted ordinary session gets in
    /// <c>WorkspaceAgentGatewayWakeTests.Wake_OnASessionPaneWhoseRuntimeNeverStarted_IsRefused</c>) is what proves
    /// the special case is gone rather than merely renamed.
    /// </summary>
    [Fact]
    public void WakingTheAssistant_FollowsTheSameGateAsAnySession_RatherThanARefusalThatNeverLooked()
    {
        var (gateway, caller) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit();
            var session = new SessionViewModel { WorkspaceId = "desk-a" };
            cockpit.Sessions.Add(session);
            var assistant = cockpit.CreateAssistantSession(AssistantIdentity.PaneId);
            if (assistant is not null)
            {
                assistant.SessionStatus = SessionStatus.Idle;
            }

            return (_Gateway(cockpit), session);
        });

        var outcome = Dispatcher.UIThread.Invoke(
            () => gateway.TryWakeAsync(caller.PaneId, AssistantIdentity.PaneId, "heads-up").GetAwaiter().GetResult());

        // Idle and resolvable, but its runtime was never started — CannotTakeATurn, not PaneGone and not
        // NotWakeable (which no longer exists in AgentWakeOutcome at all).
        Assert.Equal(AgentWakeOutcome.CannotTakeATurn, outcome);
    }

    /// <summary>
    /// Criterion 3, end to end and with no model in it: an agent notifies the assistant, and the assistant is handed
    /// it on its next tool result — nobody polled, nobody relayed. Real gateway, so the roster is what makes it land.
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

            return (_Gateway(cockpit), session.PaneId);
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

    private static WorkspaceAgentGateway _Gateway(CockpitViewModel cockpit) =>
        new(cockpit, NullLogger<WorkspaceAgentGateway>.Instance);

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
