using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The two halves of <see cref="AssistantAgentGateway"/> that only a real dispatcher can show (AC-545): that a call
/// arriving off the UI thread is marshalled onto it, and what creating a desk actually does.
/// </summary>
/// <remarks>
/// <b>Why a real dispatcher is the whole point.</b> The branch that matters in production is the marshalling one: an
/// MCP tool call arrives on a Kestrel request thread, and <c>CockpitViewModel.Sessions</c> and the workspace settings
/// are UI-thread state. Without an Avalonia application there is no dispatcher anyone pumps, and that branch either
/// collapses into the inline one (<c>CheckAccess()</c> answers true on whichever thread touched the dispatcher first)
/// or queues onto a loop nobody runs and hangs the test host — which is why the rest of the gateway's tests moved
/// here too, into <see cref="AssistantAgentGatewayTests"/>. What is left in this file is the part that needs more
/// than marshalling: a spawn crossing threads on purpose, and what creating a desk actually does.
/// </remarks>
[Collection("avalonia")]
public class AssistantAgentGatewayUiThreadTests
{
    [Fact]
    public async Task ACallArrivingOffTheUiThread_IsMarshalledOntoIt_RatherThanReadingTheCockpitFromAnotherThread()
    {
        var (gateway, deskId) = Dispatcher.UIThread.Invoke(() => _Gateway());

        var rows = await Task.Run(async () =>
        {
            // The premise of the test: without this the assertion below would be about the inline branch again.
            Assert.False(Dispatcher.UIThread.CheckAccess());

            // Awaited with a timeout rather than plainly: the failure mode being guarded against is a deadlock, and
            // a hanging test reports as an infrastructure problem hours later rather than as this feature's bug.
            return await gateway.ListWorkspacesAsync().WaitAsync(TimeSpan.FromSeconds(10));
        });

        Assert.Contains(rows, row => row.Id == deskId);
    }

    /// <summary>
    /// An embedded session — an Autopilot step, a plugin run — is a session but not a pane the cockpit closes, and
    /// the assistant's own <c>list_sessions</c> shows it. Stopping one must be refused with a reason.
    /// </summary>
    /// <remarks>
    /// The defect this pins is silent, which is why it is worth a test with this much setup: looking the pane up
    /// through <c>FindSession</c> finds it, <c>CloseSessionAsync</c> begins with <c>Sessions.IndexOf</c> and returns
    /// without doing anything for a session it does not hold, and the gateway would then have written "Stopped" to
    /// the trail and had the assistant say so — while the session kept running and kept spending.
    /// </remarks>
    [Fact]
    public async Task StoppingASessionThatRunsInsideAWorkspacesOwnSurface_IsRefused_AndItKeepsRunning()
    {
        var (gateway, cockpit, embeddedPaneId) = Dispatcher.UIThread.Invoke(() =>
        {
            var (built, _) = _Gateway(out var cockpit);
            var embedded = cockpit.Embed("plugin-desk", new EmbeddedSessionRequest());
            return (built, cockpit, embedded!.PaneId);
        });

        var result = await Dispatcher.UIThread.InvokeAsync(() => gateway.StopAsync(embeddedPaneId));

        Assert.False(result.Ok);
        Assert.Contains("own surface", result.Error);

        // The half that makes this more than a wording test: it is still there afterwards.
        Assert.NotNull(Dispatcher.UIThread.Invoke(() => cockpit.FindSession(embeddedPaneId)));
    }

    private static (AssistantAgentGateway Gateway, string DeskId) _Gateway() => _Gateway(out _);

    /// <summary>
    /// A cockpit that can actually embed a session, because one test needs a pane that is a session and is not in
    /// <c>Sessions</c> — the case the stop path used to get wrong.
    /// </summary>
    private static (AssistantAgentGateway Gateway, string DeskId) _Gateway(out CockpitViewModel cockpit)
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

        var sessions = new SessionRegistry();
        cockpit = new CockpitViewModel(
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
            terminal,
            // Embed refuses a graph without both of these; the gateway keeps its own profile store, which is what
            // the spawn path reads.
            sessionProfileStore: Substitute.For<ISessionProfileStore>(),
            sessionRegistry: sessions);

        var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
        cockpit.Workspaces.Settings = new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id };

        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        return (
            AssistantAgentGatewayGraph.Over(
                cockpit,
                sessions,
                profiles,
                Substitute.For<IAssistantSpawnAuditLog>(),
                Substitute.For<IWorkspaceAgentGateway>(),
                Substitute.For<IAgentMessageInbox>(),
                Substitute.For<IAgentNotifyAuditLog>(),
                Substitute.For<IPluginProviderRegistry>(),
                new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
                Substitute.For<IAssistantSessionHost>()),
            desk.Id);
    }
}
