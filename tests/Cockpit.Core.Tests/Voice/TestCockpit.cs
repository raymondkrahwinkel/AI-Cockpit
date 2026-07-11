using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.SessionSwitching;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.SessionSwitching;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Builds a <see cref="CockpitViewModel"/> wired entirely to substitutes/defaults for tests that only need its session selection (e.g. the voice coordinators).</summary>
internal static class TestCockpit
{
    public static CockpitViewModel NewViewModel() => NewViewModel(out _);

    /// <summary>
    /// Same as <see cref="NewViewModel()"/> but also hands back the substitute <see cref="ISessionDialogService"/>
    /// wired into the view model, for tests that need to verify a dialog was opened (e.g. a toast action
    /// invoked through <see cref="Cockpit.App.ViewModels.CockpitViewModel.OpenPluginStoreUpdatesAsync"/>, #65).
    /// </summary>
    public static CockpitViewModel NewViewModel(out ISessionDialogService dialogService)
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

        dialogService = Substitute.For<ISessionDialogService>();
        return new CockpitViewModel(
            () => new ClaudeSessionViewModel(),
            () => new ClaudeTtyViewModel(),
            dialogService,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            sessionSwitchSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore);
    }
}
