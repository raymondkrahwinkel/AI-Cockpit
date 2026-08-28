using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
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
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1201: a claim-collision check that fails must not vanish silently — the same failure mode the ticket's
/// original race produced, now guarded against so <see cref="PaneWorkspaceDirectory"/>'s own UI-thread deadline
/// (<see cref="UiUnavailableException"/>, AC-1138) can't reopen it.
/// </summary>
[Collection("avalonia")]
public class RefreshClaimCollisionsSilentFailureTests
{
    [Fact]
    public async Task UiUnavailableException_IsLoggedAsAWarning_RatherThanLeftUnobserved()
    {
        var logger = Substitute.For<ILogger<CockpitViewModel>>();
        var monitor = Substitute.For<IClaimCollisionMonitor>();
        monitor.PanesInCollision().Returns(_ => throw new UiUnavailableException(TimeSpan.FromSeconds(5)));

        var cockpit = Dispatcher.UIThread.Invoke(() => _Cockpit(monitor, logger));

        // Not observed by anything: proving the round is skipped rather than the process crashing or the caller
        // (a fire-and-forget `_ = ...` in production) being left to fault an unobserved task.
        await Dispatcher.UIThread.InvokeAsync(cockpit.RefreshClaimCollisionsAsync);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<UiUnavailableException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task AnyOtherException_IsLoggedAsAnError_RatherThanLeftUnobserved()
    {
        var logger = Substitute.For<ILogger<CockpitViewModel>>();
        var monitor = Substitute.For<IClaimCollisionMonitor>();
        var failure = new InvalidOperationException("boom");
        monitor.PanesInCollision().Returns(_ => throw failure);

        var cockpit = Dispatcher.UIThread.Invoke(() => _Cockpit(monitor, logger));

        await Dispatcher.UIThread.InvokeAsync(cockpit.RefreshClaimCollisionsAsync);

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            failure,
            Arg.Any<Func<object, Exception?, string>>());
    }

    private static CockpitViewModel _Cockpit(IClaimCollisionMonitor monitor, ILogger<CockpitViewModel> logger)
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
            terminal,
            sessionProfileStore: Substitute.For<ISessionProfileStore>(),
            claimCollisionMonitor: monitor,
            logger: logger);
    }
}
