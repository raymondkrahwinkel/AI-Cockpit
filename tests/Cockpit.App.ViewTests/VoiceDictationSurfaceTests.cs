using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.QuickNotes;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-557: dictating into an SDK session produced nothing and said nothing — failures went to a status property bound nowhere.
[Collection("avalonia")]
public class VoiceDictationSurfaceTests
{
    // Asserted against a second session, because "the right one" is only a claim when there is a wrong one to land in.
    [Fact]
    public void BothRoutes_PutTheWordsInTheSelectedSessionsComposer() => HeadlessAvalonia.Run(() =>
    {
        var transcriber = _Transcriber("open the file");
        var pill = _NewPill();
        var selected = _SdkSession(transcriber, pill);
        var other = _SdkSession(transcriber, pill);
        var cockpit = new CockpitViewModel { SelectedSession = selected };
        var view = new SessionView { DataContext = selected };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();

        // The in-window route: a real F9 press and release through the view's own handlers.
        _Hold(view);
        Dispatcher.UIThread.RunJobs();
        var afterTheLocalRoute = selected.InputText;

        selected.InputText = string.Empty;

        // The desktop-wide route, on the same selection.
        var coordinator = _Coordinator(cockpit, pill, transcriber);
        coordinator.HandleHoldStarted();
        coordinator.HandleHoldEndedAsync().GetAwaiter().GetResult();
        var afterTheGlobalRoute = selected.InputText;

        window.Close();

        Assert.Equal("open the file", afterTheLocalRoute);
        Assert.Equal("open the file", afterTheGlobalRoute);
        Assert.Empty(other.InputText);
    });

    // Criterion 5: the release for a press that opened no microphone must not end a hold — ending one that never began throws.
    [Fact]
    public void AReleaseThatNoPressStarted_NeverReachesTheTranscriber() => HeadlessAvalonia.Run(() =>
    {
        var transcriber = _Transcriber("open the file");
        var session = _SdkSession(transcriber, _NewPill());
        session.VoiceEnabled = false;
        var view = new SessionView { DataContext = session };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();

        _Hold(view);
        Dispatcher.UIThread.RunJobs();

        window.Close();

        transcriber.DidNotReceive().EndHoldAsync(Arg.Any<CancellationToken>());
    });

    // Criterion 2: asserted on the rendered pill, since the old status property was set on every path and bound nowhere.
    [Fact]
    public void AFailedDictation_IsRenderedOnThePill() => HeadlessAvalonia.Run(() =>
    {
        var overlay = new VoiceOverlayViewModel
        {
            StatusText = "No speech heard — hold the key while you talk, then let go.",
            State = VoiceOverlayState.Failed,
        };
        var window = new VoiceOverlayWindow { DataContext = overlay };
        window.Show();
        window.UpdateLayout();

        var onScreen = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible)
            .Select(text => text.Text)
            .ToList();

        window.Close();

        Assert.Contains("No speech heard — hold the key while you talk, then let go.", onScreen);
    });

    private static void _Hold(SessionView view)
    {
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F9 });
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.F9 });
    }

    private static IVoicePushToTalkService _Transcriber(string transcript)
    {
        var transcriber = Substitute.For<IVoicePushToTalkService>();
        transcriber.BeginHold().Returns(true);
        transcriber.EndHoldAsync(Arg.Any<CancellationToken>()).Returns(transcript);
        return transcriber;
    }

    private static SessionViewModel _SdkSession(IVoicePushToTalkService transcriber, VoiceOverlayCoordinator pill)
    {
        var settings = Substitute.For<IVoiceSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F9", GlobalPushToTalk = false });
        return new SessionViewModel(
            Substitute.For<ISessionManager>(), transcriber, settings, voiceOverlay: pill);
    }

    private static VoiceOverlayCoordinator _NewPill() =>
        new(new VoiceOverlayViewModel(), Substitute.For<IVoiceOverlayPresenter>());

    private static VoicePushToTalkCoordinator _Coordinator(
        CockpitViewModel cockpit, VoiceOverlayCoordinator pill, IVoicePushToTalkService transcriber)
    {
        var voiceSettings = Substitute.For<IVoiceSettingsStore>();
        voiceSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        var screenshotSettings = Substitute.For<IScreenshotSettingsStore>();
        var assistantSettings = Substitute.For<IAssistantSettingsStore>();
        var hotkeys = new GlobalHotkeyCoordinator(
            Substitute.For<IGlobalHotkeyService>(),
            voiceSettings,
            screenshotSettings,
            assistantSettings,
            Substitute.For<IQuickNoteSettingsStore>(),
            Substitute.For<IHotkeyExclusivityGuard>(),
            Substitute.For<IToastService>(),
            NullLogger<GlobalHotkeyCoordinator>.Instance);

        return new VoicePushToTalkCoordinator(
            hotkeys, cockpit, pill, transcriber, NullLogger<VoicePushToTalkCoordinator>.Instance);
    }
}
