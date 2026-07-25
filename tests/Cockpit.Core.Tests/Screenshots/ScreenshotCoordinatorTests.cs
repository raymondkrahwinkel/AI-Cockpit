using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Tests.Hotkeys;
using Cockpit.Core.Tests.Voice;
using Cockpit.Core.Toasts;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// <see cref="ScreenshotCoordinator"/> ties the OS picker to the session in view (AC-220). What it owes the
/// operator is an answer: they pressed a key, so every way this can end except an ordinary cancel has to say
/// something. What a chat panel then does with the image is <c>ScreenshotInjectionTests</c>'.
/// </summary>
public class ScreenshotCoordinatorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    [Fact]
    public async Task ACapturedScreenshot_LandsOnTheSelectedSession()
    {
        var session = new RecordingSession();
        var coordinator = _Create(new FakeScreenshotCapture { Result = Png }, session, out var toasts);

        await coordinator.CaptureIntoSelectedSessionAsync();

        session.InjectedScreenshots.Should().ContainSingle().Which.Should().Equal(Png);
        toasts.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    /// <summary>Pressing Escape on the picker is the ordinary way to change your mind; a toast for it would be nagging.</summary>
    [Fact]
    public async Task ACancelledPicker_SaysNothing()
    {
        var session = new RecordingSession();
        var coordinator = _Create(new FakeScreenshotCapture { Result = null }, session, out var toasts);

        await coordinator.CaptureIntoSelectedSessionAsync();

        session.InjectedScreenshots.Should().BeEmpty();
        toasts.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    /// <summary>
    /// Holding the key over a cockpit with nothing open is the screenshot's version of the push-to-talk bug
    /// (#34): without this it opens the desktop's picker, the operator drags a region, and the image goes
    /// nowhere with nothing said.
    /// </summary>
    [Fact]
    public async Task WithNoSessionSelected_ItSaysSo_AndNeverOpensThePicker()
    {
        var capture = new FakeScreenshotCapture { Result = Png };
        var coordinator = _Create(capture, session: null, out var toasts);

        await coordinator.CaptureIntoSelectedSessionAsync();

        capture.CaptureCallCount.Should().Be(0);
        toasts.Received(1).Show(Arg.Is<string>(message => message.Contains("session")), ToastSeverity.Warning);
    }

    /// <summary>A session that cannot carry an image gets its reason relayed — the seam returns it, and this is what puts it in front of the operator.</summary>
    [Fact]
    public async Task ASessionThatCannotTakeImages_HasItsReasonShown()
    {
        var session = new RecordingSession { RefusalReason = "a pty carries text only" };
        var coordinator = _Create(new FakeScreenshotCapture { Result = Png }, session, out var toasts);

        await coordinator.CaptureIntoSelectedSessionAsync();

        toasts.Received(1).Show("a pty carries text only", ToastSeverity.Warning);
    }

    /// <summary>A portal that refuses, a helper that will not start: not a cancel, and not something to swallow.</summary>
    [Fact]
    public async Task ACaptureThatFails_IsReported_RatherThanLookingLikeACancel()
    {
        var coordinator = _Create(
            new FakeScreenshotCapture { Failure = new InvalidOperationException("the portal said no") },
            new RecordingSession(),
            out var toasts);

        var act = async () => await coordinator.CaptureIntoSelectedSessionAsync();

        await act.Should().NotThrowAsync("both callers discard the task");
        toasts.Received(1).Show(Arg.Is<string>(message => message.Contains("the portal said no")), ToastSeverity.Error);
    }

    /// <summary>
    /// The hotkey is easy to press twice while the picker is already open. A second picker over the first is
    /// the desktop's problem to render and the operator's to dismiss — so there is only ever one.
    /// </summary>
    [Fact]
    public async Task PressingItAgainWhileThePickerIsOpen_StartsNoSecondCapture()
    {
        var session = new RecordingSession();
        // The capture has to reach back into the coordinator that owns it, so the second press happens while the
        // first is still open. A holder rather than a captured local: the coordinator does not exist yet when the
        // fake is built, and a null-forgiving `!` on that would be exactly the compiler protection this codebase
        // does not give up (CSharp.md).
        var pressedAgain = new CaptureReentry();
        var capture = new FakeScreenshotCapture { Result = Png, WhileCapturing = pressedAgain.InvokeAsync };
        var coordinator = _Create(capture, session, out _);
        pressedAgain.Coordinator = coordinator;

        await coordinator.CaptureIntoSelectedSessionAsync();

        capture.CaptureCallCount.Should().Be(1);
        session.InjectedScreenshots.Should().ContainSingle();
    }

    /// <summary>The button reads this to disable itself with a reason rather than offering a capture the platform cannot do.</summary>
    [Fact]
    public void OnAPlatformThatCannotCapture_ItSaysSo()
    {
        var coordinator = _Create(new FakeScreenshotCapture { IsSupported = false }, session: null, out _);

        coordinator.IsSupported.Should().BeFalse();
    }

    private static ScreenshotCoordinator _Create(
        FakeScreenshotCapture capture, SessionPanelViewModel? session, out IToastService toasts)
    {
        var cockpit = TestCockpit.NewViewModel();
        cockpit.SelectedSession = session;
        toasts = Substitute.For<IToastService>();

        return new ScreenshotCoordinator(
            TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService()),
            capture,
            cockpit,
            toasts,
            NullLogger<ScreenshotCoordinator>.Instance);
    }
}
