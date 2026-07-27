using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Tests.Hotkeys;
using Cockpit.Core.Tests.Voice;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// A platform that cannot say straight away whether it can capture (AC-326). On Linux the answer is a D-Bus
/// round trip, and the cockpit assigns <see cref="CockpitViewModel.Screenshots"/> in the same statement that
/// builds the coordinator — so the first read is always "cannot", and a session that was already open would
/// keep a greyed-out button for the rest of the run if nobody came back to ask again.
/// </summary>
public class ScreenshotSupportSettlingTests
{
    [Fact]
    public async Task ASessionOpenBeforeThePlatformAnswers_GetsItsButtonBackWhenItDoes()
    {
        var answered = new TaskCompletionSource();
        var capture = new FakeScreenshotCapture { IsSupported = false, SupportSettled = answered.Task };
        var cockpit = TestCockpit.NewViewModel();
        var session = new SessionViewModel();
        cockpit.Sessions.Add(session);

        cockpit.Screenshots = _Coordinator(capture, cockpit);
        session.ScreenshotRefusalReason.Should().NotBeNull("nothing is known yet, and claiming support would be a guess");

        capture.IsSupported = true;
        answered.SetResult();
        await capture.SupportSettled;
        await Task.Yield();

        session.ScreenshotRefusalReason.Should().BeNull("the desktop answered, and the button it belongs to is still on screen");
    }

    /// <summary>The desktop really has no portal. Coming back to ask does not turn that into a yes.</summary>
    [Fact]
    public async Task APlatformThatAnswersNo_LeavesTheButtonOffWithItsReason()
    {
        var answered = new TaskCompletionSource();
        var capture = new FakeScreenshotCapture { IsSupported = false, SupportSettled = answered.Task };
        var cockpit = TestCockpit.NewViewModel();
        var session = new SessionViewModel();
        cockpit.Sessions.Add(session);

        cockpit.Screenshots = _Coordinator(capture, cockpit);
        answered.SetResult();
        await capture.SupportSettled;
        await Task.Yield();

        session.ScreenshotRefusalReason.Should().Contain("not available on this platform");
        session.CanCaptureScreenshot.Should().BeFalse();
    }

    /// <summary>A platform that knows outright — Windows, macOS — answers on the first pass and never comes back.</summary>
    [Fact]
    public void APlatformThatKnowsOutright_NeedsNoSecondPass()
    {
        var cockpit = TestCockpit.NewViewModel();
        var session = new SessionViewModel();
        cockpit.Sessions.Add(session);

        cockpit.Screenshots = _Coordinator(new FakeScreenshotCapture { IsSupported = true }, cockpit);

        session.ScreenshotRefusalReason.Should().BeNull();
    }

    private static ScreenshotCoordinator _Coordinator(FakeScreenshotCapture capture, CockpitViewModel cockpit) =>
        new(TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService()),
            capture,
            cockpit,
            Substitute.For<IToastService>(),
            new FakeScreenshotSettingsStore(),
            new FakeScreenshotImageEditor(),
            StubDesktopWindows.None,
            NullLogger<ScreenshotCoordinator>.Instance);
}
