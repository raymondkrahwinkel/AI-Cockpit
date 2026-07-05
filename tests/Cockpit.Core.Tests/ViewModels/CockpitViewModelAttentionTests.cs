using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The edge-triggered attention wiring on <see cref="CockpitViewModel"/>: entering
/// <see cref="SessionStatus.NeedsAttention"/> fires the notifier exactly once, and only on the edge —
/// not again while the session stays in that state, and not after the session is closed.
/// </summary>
public class CockpitViewModelAttentionTests
{
    private readonly IAttentionNotifier _attentionNotifier = Substitute.For<IAttentionNotifier>();

    [Fact]
    public void EnteringNeedsAttention_FiresTheNotifierOnce()
    {
        var vm = NewVm();
        var session = vm.Sessions[0];

        session.SessionStatus = SessionStatus.NeedsAttention;

        _attentionNotifier.Received(1).NotifyAttentionAsync(
            Arg.Is<AttentionNotification>(n => n.Title == session.Title && n.Body == "Needs attention"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void StayingInNeedsAttention_DoesNotRefire_OnAFurtherStatusTouch()
    {
        var vm = NewVm();
        var session = vm.Sessions[0];

        session.SessionStatus = SessionStatus.NeedsAttention;
        session.SessionStatus = SessionStatus.Busy;
        session.SessionStatus = SessionStatus.NeedsAttention;

        // Two distinct edges into NeedsAttention → two notifications, not one per property change.
        _attentionNotifier.Received(2).NotifyAttentionAsync(Arg.Any<AttentionNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClosedSession_NoLongerFires()
    {
        var vm = NewVm();
        var session = vm.Sessions[0];

        await vm.CloseSessionCommand.ExecuteAsync(session);
        session.SessionStatus = SessionStatus.NeedsAttention;

        await _attentionNotifier.DidNotReceiveWithAnyArgs().NotifyAttentionAsync(default!, default);
    }

    private CockpitViewModel NewVm()
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        return new CockpitViewModel(
            () => new ClaudeSessionViewModel(),
            () => new ClaudeTtyViewModel(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            _attentionNotifier,
            notificationSettingsStore);
    }
}
