using FluentAssertions;
using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// The composer's screenshot button (AC-220) must not lie about what works: it is enabled exactly when a
/// capture would land, and its tooltip carries the same sentence the hotkey path would have shown as a toast.
/// One refusal, two surfaces — so the button and the key can never disagree.
/// </summary>
public class ScreenshotButtonStateTests
{
    [Fact]
    public void WithNothingWiredToRunIt_TheButtonIsOff()
    {
        var session = _CreateSdkSession();

        session.CanCaptureScreenshot.Should().BeFalse("there is no picker to open in a design-time or test graph");
    }

    [Fact]
    public void AVisionSessionOnACapablePlatform_HasTheButtonOn()
    {
        var session = _CreateSdkSession();
        session.ScreenshotCapture = _ => Task.CompletedTask;

        session.CanCaptureScreenshot.Should().BeTrue();
        session.ScreenshotTooltip.Should().Be("Take a screenshot into this session");
    }

    /// <summary>The grey button explains itself, rather than leaving the operator to guess why it will not click.</summary>
    [Fact]
    public void ANonVisionSession_HasTheButtonOff_WithTheReasonOnIt()
    {
        var session = _CreateSdkSession();
        session.ScreenshotCapture = _ => Task.CompletedTask;
        session.Capabilities = SessionCapabilities.ClaudeCli with { SupportsVision = false };

        session.CanCaptureScreenshot.Should().BeFalse();
        session.ScreenshotTooltip.Should().Contain("image input");
    }

    [Fact]
    public void ATerminalSession_HasTheButtonOff_WithTheReasonOnIt()
    {
        var session = _CreateTtySession();
        session.ScreenshotCapture = _ => Task.CompletedTask;

        session.CanCaptureScreenshot.Should().BeFalse();
        session.ScreenshotTooltip.Should().Contain("terminal");
    }

    /// <summary>A platform with no capture at all is the button's business too — macOS has the button and no hotkey, a hypothetical third OS has neither.</summary>
    [Fact]
    public void OnAPlatformWithoutCapture_TheButtonIsOff_WithTheReasonOnIt()
    {
        var session = _CreateSdkSession();
        session.ScreenshotCapture = _ => Task.CompletedTask;
        session.ScreenshotPlatformRefusal = "Screen capture is not available on this platform.";

        session.CanCaptureScreenshot.Should().BeFalse();
        session.ScreenshotTooltip.Should().Contain("platform");
    }

    /// <summary>
    /// The vision capability is only known once the driver has started and reported it, so the button starts
    /// enabled (the Claude-CLI default) and has to follow when a local provider turns out not to see images.
    /// Without the notification it stays clickable and silently refuses.
    /// </summary>
    /// <remarks>
    /// Driven by setting <c>Capabilities</c> — the thing the driver actually reports — rather than by calling the
    /// notify helper, which would pass whether or not anything is wired to it. Red without the
    /// <c>OnCapabilitiesChanged</c> hook.
    /// </remarks>
    [Fact]
    public void WhenTheDriverReportsItCannotSeeImages_TheButtonFollows()
    {
        var session = _CreateSdkSession();
        session.ScreenshotCapture = _ => Task.CompletedTask;
        session.CanCaptureScreenshot.Should().BeTrue("the Claude-CLI default is the starting point");
        var canCaptureChanged = false;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionPanelViewModel.CanCaptureScreenshot))
            {
                canCaptureChanged = true;
            }
        };

        session.Capabilities = SessionCapabilities.ClaudeCli with { SupportsVision = false };

        canCaptureChanged.Should().BeTrue("the button has to hear that the driver cannot see images");
        session.CanCaptureScreenshot.Should().BeFalse();
    }

    private static SessionViewModel _CreateSdkSession()
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings());
        return new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()),
            Substitute.For<IVoicePushToTalkService>(),
            voiceSettingsStore);
    }

    private static TtyViewModel _CreateTtySession()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return new TtyViewModel(Substitute.For<ITtyLauncher>(), resolver);
    }
}
