using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
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
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A voice or language chosen in Options → Voice reaches the assistant, and not only the sessions in the grid.
/// </summary>
/// <remarks>
/// The fan-out walks <c>Sessions</c>, and the assistant is in neither that collection nor the embedded table by
/// construction — so a new voice reached every ordinary session and left the one surface whose entire output is
/// speech talking in the old one until it was restarted. The "Hear it" button played the new voice at the moment of
/// choosing, which is what made the assistant's disagreement read as a fault in the voice rather than in the save.
/// <para>
/// The same class of defect as the consent routing next door (<see cref="AssistantConsentRoutingTests"/>): a loop
/// over "all sessions" that does not include the assistant.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class AssistantVoiceFanOutTests
{
    [Fact]
    public void SavingTheVoiceSettings_PushesTheNewVoiceAndLanguage_ToTheAssistantToo()
    {
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());

        var (assistant, gridSession) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit(voice);
            var assistantSession = cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

            // An ordinary session alongside it: without this the test would still pass on a fan-out that reached
            // nothing at all, and "the assistant is missed" is only a finding next to a session that is not.
            var grid = new SessionViewModel();
            cockpit.Sessions.Add(grid);

            cockpit.SelectedTtsVoice = TtsVoiceCatalog.Voices[^1];
            cockpit.SelectedReadAloudLanguage = new SttLanguageOption("Dutch", "nl");
            cockpit.SaveVoiceSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            return (assistantSession, grid);
        });

        Assert.NotNull(assistant);
        Assert.Equal(TtsVoiceCatalog.Voices[^1].Sid, gridSession.TtsVoiceSid);
        Assert.Equal("nl", gridSession.ReadAloudLanguage);

        Assert.Equal(TtsVoiceCatalog.Voices[^1].Sid, assistant!.TtsVoiceSid);
        Assert.Equal("nl", assistant.ReadAloudLanguage);
    }

    private static CockpitViewModel _Cockpit(IVoiceSettingsStore voice)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
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
