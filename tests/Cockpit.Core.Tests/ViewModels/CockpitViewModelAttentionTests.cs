using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionSwitching;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.SessionSwitching;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Layout;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The edge-triggered attention wiring on <see cref="CockpitViewModel"/>: entering
/// <see cref="SessionStatus.NeedsAttention"/> fires the notifier exactly once, and only on the edge —
/// not again while the session stays in that state, and not after the session is closed. A session is
/// created through the New-session dialog (faked), since #31 the constructor no longer seeds one.
/// </summary>
public class CockpitViewModelAttentionTests
{
    private readonly IAttentionNotifier _attentionNotifier = Substitute.For<IAttentionNotifier>();

    [Fact]
    public async Task EnteringNeedsAttention_FiresTheNotifierOnce()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];

        session.SessionStatus = SessionStatus.NeedsAttention;

        await _attentionNotifier.Received(1).NotifyAttentionAsync(
            Arg.Is<AttentionNotification>(n => n.Title == session.Title && n.Body == "Needs attention"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StayingInNeedsAttention_DoesNotRefire_OnAFurtherStatusTouch()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];

        session.SessionStatus = SessionStatus.NeedsAttention;
        session.SessionStatus = SessionStatus.Busy;
        session.SessionStatus = SessionStatus.NeedsAttention;

        // Two distinct edges into NeedsAttention → two notifications, not one per property change.
        await _attentionNotifier.Received(2).NotifyAttentionAsync(Arg.Any<AttentionNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClosedSession_NoLongerFires()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];

        await vm.CloseSessionCommand.ExecuteAsync(session);
        session.SessionStatus = SessionStatus.NeedsAttention;

        await _attentionNotifier.DidNotReceiveWithAnyArgs().NotifyAttentionAsync(default!, default);
    }

    private CockpitViewModel NewVm()
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var sessionSwitchSettingsStore = Substitute.For<ISessionSwitchSettingsStore>();
        sessionSwitchSettingsStore.LoadAsync().Returns(new SessionSwitchSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(new NewSessionResult(
            SessionKind.Sdk,
            new ClaudeProfile("default", @"C:\fake\.claude"),
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort, null));

        return new CockpitViewModel(
            () => new ClaudeSessionViewModel(),
            () => new ClaudeTtyViewModel(),
            dialogService,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            _attentionNotifier,
            notificationSettingsStore,
            sessionSwitchSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore);
    }
}
