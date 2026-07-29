using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-410 step 3: an AI session's <see cref="WorkspacePane"/> record is written to <c>Workspaces</c> as soon as
/// the panel is attached — before it starts — and removed again on close. A plain terminal
/// (<see cref="CockpitViewModel.NewTerminal"/>) never gets one, since <c>PaneKind.Terminal</c> persistence is out
/// of scope for this ticket (the design's snijlijn).
/// </summary>
public class SessionPanePersistenceTests
{
    [Fact]
    public async Task NewSession_WritesAnAiSessionPane_MatchingWhatTheSessionStartedWith()
    {
        var vm = NewVm();

        await vm.NewSessionCommand.ExecuteAsync(null);

        var session = vm.Sessions[0];
        var pane = vm.Workspaces.Active!.Panes.Single(p => p.Id == session.PaneId);
        Assert.Equal(PaneKind.AiSession, pane.Kind);
        Assert.Equal("default", pane.ProfileId);
        Assert.Equal(PaneSessionKind.Sdk, pane.SessionKind);
    }

    [Fact]
    public async Task NewSession_WhenTheDialogPicksTty_WritesAPaneWithTtySessionKind()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);

        await vm.NewSessionCommand.ExecuteAsync(null);

        var session = vm.Sessions[0];
        var pane = vm.Workspaces.Active!.Panes.Single(p => p.Id == session.PaneId);
        Assert.Equal(PaneSessionKind.Tty, pane.SessionKind);
    }

    [Fact]
    public async Task CloseSession_RemovesThePersistedPane()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        var workspaceId = session.WorkspaceId;

        await vm.CloseSessionCommand.ExecuteAsync(session);

        Assert.DoesNotContain(vm.Workspaces.Settings.Workspaces.Single(w => w.Id == workspaceId).Panes, p => p.Id == session.PaneId);
    }

    [Fact]
    public async Task CloseWorkspace_RemovesEveryPaneItHeld()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        var workspaceId = session.WorkspaceId;

        await vm.Workspaces.CloseWorkspaceCommand.ExecuteAsync(workspaceId);

        Assert.DoesNotContain(vm.Workspaces.Settings.Workspaces, w => w.Id == workspaceId);
    }

    private static CockpitViewModel NewVm(ISessionDialogService? dialogService = null)
    {
        var captureService = Substitute.For<IAudioCaptureService>();
        var playbackService = Substitute.For<IAudioPlaybackService>();
        var attentionNotifier = Substitute.For<IAttentionNotifier>();
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<Cockpit.Core.Abstractions.Terminal.ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogService ?? DefaultDialogService(),
            captureService,
            playbackService,
            attentionNotifier,
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore);
    }

    private static ISessionDialogService DefaultDialogService()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Sdk));
        return dialogService;
    }

    private static NewSessionResult NewSessionResultFor(SessionKind kind) => new(
        kind,
        new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
        SessionOptionCatalog.DefaultPermissionMode,
        SessionOptionCatalog.DefaultModel,
        SessionOptionCatalog.DefaultEffort, null);
}
