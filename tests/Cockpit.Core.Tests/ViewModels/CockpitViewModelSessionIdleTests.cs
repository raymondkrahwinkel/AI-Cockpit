using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Layout;
using Cockpit.Core.Voice;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Sessions finishing and falling quiet: a completed turn announces itself (the notifier decides whether you
/// were watching), a session that has been done and quiet past the threshold drops back to idle, and the
/// "everything is quiet" message goes out once — not on every sweep of a cockpit nobody is using.
/// </summary>
public class CockpitViewModelSessionIdleTests
{
    private readonly IAttentionNotifier _attentionNotifier = Substitute.For<IAttentionNotifier>();

    // The sweep is measured from the session's own last activity, which the view model stamps with the real
    // clock. A fixed "now" here was a time bomb: it passed until the wall clock caught up with the date it
    // hardcoded, and then every one of these tests failed for a reason that had nothing to do with idling.
    private static DateTimeOffset Quiet(SessionPanelViewModel session, TimeSpan since) => session.LastActivityUtc + since;

    [Fact]
    public async Task FinishingATurn_NotifiesWithTheSelectionAndWindowState()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        vm.IsWindowActive = false;

        session.SessionStatus = SessionStatus.Busy;
        session.SessionStatus = SessionStatus.Done;

        await _attentionNotifier.Received(1).NotifySessionFinishedAsync(
            Arg.Is<AttentionNotification>(notification => notification.Title == session.Title && notification.Body == "Done"),
            true,
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReachingDoneWithoutHavingBeenBusy_DoesNotNotify()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);

        // Only a turn that was actually running and then completed is news; anything else landing on Done
        // (a restored session, say) never ran while you waited for it.
        vm.Sessions[0].SessionStatus = SessionStatus.Done;

        await _attentionNotifier.DidNotReceiveWithAnyArgs().NotifySessionFinishedAsync(default!, default, default, default);
    }

    [Fact]
    public async Task FinishedSession_QuietPastTheThreshold_GoesIdleAndSaysSo()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        vm.SessionIdleMinutes = 5;
        session.SessionStatus = SessionStatus.Done;

        vm.SweepIdleSessions(Quiet(session, TimeSpan.FromMinutes(30)));

        session.SessionStatus.Should().Be(SessionStatus.Idle);
        await _attentionNotifier.Received(1).NotifySessionIdleAsync(
            Arg.Is<AttentionNotification>(notification => notification.Title == session.Title),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BusySession_NeverGoesIdle_HoweverLongTheSweepWaits()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        vm.SessionIdleMinutes = 5;
        session.SessionStatus = SessionStatus.Busy;

        vm.SweepIdleSessions(Quiet(session, TimeSpan.FromHours(3)));

        session.SessionStatus.Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public async Task LastSessionGoingIdle_AnnouncesEverythingIsQuiet_Once()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        vm.SessionIdleMinutes = 5;
        vm.Sessions[0].SessionStatus = SessionStatus.Done;

        vm.SweepIdleSessions(Quiet(vm.Sessions[0], TimeSpan.FromMinutes(30)));
        vm.SweepIdleSessions(Quiet(vm.Sessions[0], TimeSpan.FromMinutes(31)));

        // A cockpit left alone keeps sweeping; it must not keep repeating that nothing is happening.
        await _attentionNotifier.Received(1).NotifyAllSessionsIdleAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WorkStartingAgain_ArmsTheEverythingIsQuietMessageForNextTime()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        vm.SessionIdleMinutes = 5;

        session.SessionStatus = SessionStatus.Done;
        vm.SweepIdleSessions(Quiet(session, TimeSpan.FromMinutes(30)));

        session.SessionStatus = SessionStatus.Busy;
        session.SessionStatus = SessionStatus.Done;
        vm.SweepIdleSessions(Quiet(session, TimeSpan.FromMinutes(60)));

        await _attentionNotifier.Received(2).NotifyAllSessionsIdleAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ZeroThreshold_LeavesFinishedSessionsOnDone()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        vm.SessionIdleMinutes = 0;
        session.SessionStatus = SessionStatus.Done;

        vm.SweepIdleSessions(Quiet(session, TimeSpan.FromHours(3)));

        session.SessionStatus.Should().Be(SessionStatus.Done);
        await _attentionNotifier.DidNotReceiveWithAnyArgs().NotifySessionIdleAsync(default!, default);
    }

    private CockpitViewModel NewVm()
    {
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
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(new NewSessionResult(
            SessionKind.Sdk,
            new SessionProfile("default", @"C:\fake\.claude"),
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort, null));

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new ClaudeTtyViewModel(),
            dialogService,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            _attentionNotifier,
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore);
    }
}
