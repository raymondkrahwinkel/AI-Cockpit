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

        Assert.False(session.CanCaptureScreenshot, "there is no picker to open in a design-time or test graph");
    }

    [Fact]
    public void AVisionSessionOnACapablePlatform_HasTheButtonOn()
    {
        var session = _CreateSdkSession();
        session.ScreenshotCapture = _ => Task.CompletedTask;

        Assert.True(session.CanCaptureScreenshot);
        Assert.Equal("Take a screenshot into this session", session.ScreenshotTooltip);
    }

    /// <summary>The grey button explains itself, rather than leaving the operator to guess why it will not click.</summary>
    [Fact]
    public void ANonVisionSession_HasTheButtonOff_WithTheReasonOnIt()
    {
        var session = _CreateSdkSession();
        session.ScreenshotCapture = _ => Task.CompletedTask;
        session.Capabilities = SessionCapabilities.ClaudeCli with { SupportsVision = false };

        Assert.False(session.CanCaptureScreenshot);
        Assert.Contains("image input", session.ScreenshotTooltip);
    }

    /// <summary>A terminal session can take one (AC-226) — its agent reads the file the path points at — so the button is live like any other.</summary>
    [Fact]
    public void ATerminalSession_HasTheButtonOn()
    {
        var session = _CreateTtySession();
        session.ScreenshotCapture = _ => Task.CompletedTask;

        Assert.True(session.CanCaptureScreenshot);
    }

    /// <summary>A platform with no capture at all is the button's business too — macOS has the button and no hotkey, a hypothetical third OS has neither.</summary>
    [Fact]
    public void OnAPlatformWithoutCapture_TheButtonIsOff_WithTheReasonOnIt()
    {
        var session = _CreateSdkSession();
        session.ScreenshotCapture = _ => Task.CompletedTask;
        session.ScreenshotPlatformRefusal = "Screen capture is not available on this platform.";

        Assert.False(session.CanCaptureScreenshot);
        Assert.Contains("platform", session.ScreenshotTooltip);
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
        Assert.True(session.CanCaptureScreenshot, "the Claude-CLI default is the starting point");
        var canCaptureChanged = false;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionPanelViewModel.CanCaptureScreenshot))
            {
                canCaptureChanged = true;
            }
        };

        session.Capabilities = SessionCapabilities.ClaudeCli with { SupportsVision = false };

        Assert.True(canCaptureChanged, "the button has to hear that the driver cannot see images");
        Assert.False(session.CanCaptureScreenshot);
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
