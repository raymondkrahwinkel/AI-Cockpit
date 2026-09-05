using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-239: the screenshot capture (AC-220) as the companion window's second first-party resident, through the
/// same host AC-238 built for the assistant indicator. What a click does with the picker is
/// <c>ScreenshotCoordinatorTests</c>' (Cockpit.Core.Tests); this only proves the button is really wired to it —
/// including the one thing the companion window cannot get away with that the composer can: a disabled button
/// still has to be there, not hidden, or disabled and missing look the same to the operator.
/// </summary>
[Collection("avalonia")]
public sealed class ScreenshotCompanionToolTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    [Fact]
    public void Click_CapturesIntoTheSelectedSession_ThroughTheFirstPartyHost() => HeadlessAvalonia.Run(() =>
    {
        var session = new RecordingScreenshotSession();
        var cockpit = new CockpitViewModel { SelectedSession = session };
        var capture = Substitute.For<IScreenshotCapture>();
        capture.IsSupported.Returns(true);
        capture.SupportSettled.Returns(Task.CompletedTask);
        capture.CaptureAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<ScreenCapture?>(new ScreenCapture
        {
            Image = Png,
            Displays = [new CapturedDisplay
            {
                DesktopBounds = new CaptureRect(0, 0, 1920, 1080),
                Scale = 1,
                ImageBounds = new CaptureRect(0, 0, 1920, 1080),
            }],
        }));
        var screenshots = _Coordinator(capture, cockpit, out _);
        var registration = screenshots.CreateCompanionTool();

        // Registered exactly the way the assistant indicator is (AC-238's naad), not a second route.
        var host = new FirstPartyCompanionToolHost(
            new CompanionToolRegistry(),
            new PluginStorage(new Dictionary<string, string>(), _ => { }),
            Substitute.For<ICockpitSessionObserver>());
        Assert.True(host.AddCompanionTool(registration));

        var button = Assert.IsType<Button>(registration.CreateView(Substitute.For<ICompanionToolContext>()));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Png, Assert.Single(session.InjectedScreenshots));
    });

    [Fact]
    public void Click_OnASessionThatRefuses_ShowsTheReasonRatherThanSayingNothing() => HeadlessAvalonia.Run(() =>
    {
        var session = new RecordingScreenshotSession { RefusalReason = "This session's provider does not support image input." };
        var cockpit = new CockpitViewModel { SelectedSession = session };
        var capture = Substitute.For<IScreenshotCapture>();
        capture.IsSupported.Returns(true);
        capture.SupportSettled.Returns(Task.CompletedTask);
        capture.CaptureAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<ScreenCapture?>(new ScreenCapture
        {
            Image = Png,
            Displays = [new CapturedDisplay
            {
                DesktopBounds = new CaptureRect(0, 0, 1920, 1080),
                Scale = 1,
                ImageBounds = new CaptureRect(0, 0, 1920, 1080),
            }],
        }));
        var screenshots = _Coordinator(capture, cockpit, out var toasts);

        var button = Assert.IsType<Button>(
            screenshots.CreateCompanionTool().CreateView(Substitute.For<ICompanionToolContext>()));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(session.InjectedScreenshots);
        toasts.Received(1).Show(session.RefusalReason, ToastSeverity.Warning);
    });

    [Fact]
    public void WhenThePlatformCannotCapture_TheButtonStaysVisibleButDisabled() => HeadlessAvalonia.Run(() =>
    {
        var capture = Substitute.For<IScreenshotCapture>();
        capture.IsSupported.Returns(false);
        capture.SupportSettled.Returns(Task.CompletedTask);
        var screenshots = _Coordinator(capture, new CockpitViewModel(), out _);

        var button = Assert.IsType<Button>(
            screenshots.CreateCompanionTool().CreateView(Substitute.For<ICompanionToolContext>()));

        // Hidden and broken read the same in the companion window (AC-238's ShowWhenDisabled reasoning) — so
        // this stays visible and says why, instead of disappearing.
        Assert.True(button.IsVisible);
        Assert.False(button.IsEnabled);
        Assert.Equal("Screen capture is not available on this platform.", ToolTip.GetTip(button));
    });

    private static ScreenshotCoordinator _Coordinator(IScreenshotCapture capture, CockpitViewModel cockpit, out IToastService toasts)
    {
        toasts = Substitute.For<IToastService>();

        var hotkeys = new GlobalHotkeyCoordinator(
            Substitute.For<IGlobalHotkeyService>(),
            Substitute.For<IVoiceSettingsStore>(),
            Substitute.For<IScreenshotSettingsStore>(),
            Substitute.For<IAssistantSettingsStore>(),
            Substitute.For<IHotkeyExclusivityGuard>(),
            Substitute.For<IToastService>(),
            NullLogger<GlobalHotkeyCoordinator>.Instance);

        return new ScreenshotCoordinator(
            hotkeys,
            capture,
            cockpit,
            toasts,
            Substitute.For<IScreenshotSettingsStore>(),
            Substitute.For<IScreenshotImageEditor>(),
            Substitute.For<IDesktopWindows>(),
            NullLogger<ScreenshotCoordinator>.Instance);
    }

    // A minimal session that records what was injected, mirroring Cockpit.Core.Tests.Screenshots.RecordingSession
    // — that one cannot be reused here, since proving the button's own click wiring needs a real Avalonia
    // platform, which is exactly the project split that class's own doc comment calls for.
    private sealed class RecordingScreenshotSession : SessionPanelViewModel
    {
        public string? RefusalReason { get; init; }

        public List<byte[]> InjectedScreenshots { get; } = [];

        protected override Task<string?> OnScreenshotCapturedAsync(byte[] screenshotPng)
        {
            if (RefusalReason is not null)
            {
                return Task.FromResult<string?>(RefusalReason);
            }

            InjectedScreenshots.Add(screenshotPng);
            return Task.FromResult<string?>(null);
        }

        protected override void OnVoiceTextReady(string text)
        {
        }

        public override Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => Task.FromResult(false);

        protected override ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
    }
}
