using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-557: dictating into an SDK session produced nothing and said nothing. Two routes reach the same hold — the
/// in-window <c>F9</c> handlers on <see cref="SessionView"/>/<see cref="TtyView"/> and the desktop-wide
/// <see cref="VoicePushToTalkCoordinator"/> — and the failures of both were written to a status property with no
/// binding anywhere. These are the view-level halves: that the two routes agree on which session gets the words,
/// and that a failure actually reaches the screen.
/// </summary>
[Collection("avalonia")]
public class VoiceDictationSurfaceTests
{
    /// <summary>
    /// Criterion 4. Two sessions open, one of them selected and shown: the words land in that one whichever route
    /// carried the key. Asserted against a second session precisely because "the right one" is only a claim when
    /// there is a wrong one to land in.
    /// </summary>
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

    /// <summary>
    /// Criterion 5, the in-window half: a release also arrives for a press that never opened a microphone — voice
    /// off for this session — and ending a hold that never began throws inside the transcriber. The handler simply
    /// does not call it. (The desktop-wide half is <c>VoicePushToTalkCoordinatorTests</c>'s, which returns on its
    /// own "nothing was recorded" flag.)
    /// </summary>
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

    /// <summary>
    /// Criterion 2: the explanation reaches an actual screen. Asserted on the rendered pill rather than on the
    /// view model, because that is the step this ticket found missing — the old status property was set on every
    /// one of these paths and had no binding anywhere in the app.
    /// </summary>
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
            Substitute.For<IHotkeyExclusivityGuard>(),
            Substitute.For<IToastService>(),
            NullLogger<GlobalHotkeyCoordinator>.Instance);

        return new VoicePushToTalkCoordinator(
            hotkeys, cockpit, pill, transcriber, NullLogger<VoicePushToTalkCoordinator>.Instance);
    }
}
