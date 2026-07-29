using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Tests.Hotkeys;
using Cockpit.Core.Tests.Voice;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// What happens between the screen being read and a session receiving it (AC-329): the operator marks out a
/// region, that region is cropped, and it is waiting for them next time. The surface itself needs a desktop to
/// be put over, so it is stood in for here — what is being checked is the flow around it.
/// </summary>
public class ScreenshotSelectionFlowTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static readonly ScreenCapture Capture = new()
    {
        Image = Png,
        Displays =
        [
            new CapturedDisplay
            {
                DesktopBounds = new CaptureRect(0, 0, 1920, 1080),
                Scale = 1.5,
                ImageBounds = new CaptureRect(0, 0, 2880, 1620),
            },
        ],
    };

    [Fact]
    public async Task TheRegionTheOperatorMarkedOut_IsWhatGetsCropped()
    {
        var region = new CaptureRect(100, 200, 640, 480);
        var (coordinator, session, editor, _) = _Flow(_ => new ScreenshotSelection { Region = region });

        await coordinator.CaptureIntoAsync(session);

        Assert.Equal(region, editor.Cropped);
        Assert.Single(session.InjectedScreenshots);
    }

    /// <summary>The same panel gets grabbed over and over, so the region outlives the capture it came from.</summary>
    [Fact]
    public async Task TheRegion_IsWaitingOnTheSurfaceNextTime()
    {
        var region = new CaptureRect(10, 20, 30, 40);
        var (coordinator, session, _, settings) = _Flow(_ => new ScreenshotSelection { Region = region });

        await coordinator.CaptureIntoAsync(session);

        Assert.Equal(region, settings.Settings.LastRegion);
    }

    [Fact]
    public async Task TheRegionFromLastTime_IsOfferedToTheSurface()
    {
        var remembered = new CaptureRect(5, 5, 50, 50);
        CaptureRect? offered = null;
        var (coordinator, session, _, settings) = _Flow(last =>
        {
            offered = last;
            return new ScreenshotSelection { Region = new CaptureRect(0, 0, 10, 10) };
        });
        await settings.SaveAsync(settings.Settings with { LastRegion = remembered });

        await coordinator.CaptureIntoAsync(session);

        Assert.Equal(remembered, offered);
    }

    /// <summary>
    /// What the operator hid is applied to the pixels that are sent, after the crop — so the boxes are in the
    /// coordinates of the picture that actually leaves the machine (AC-331).
    /// </summary>
    [Fact]
    public async Task WhatWasHidden_IsRedactedOutOfWhatIsSent()
    {
        var box = new RedactionMark(new CaptureRect(10, 10, 50, 50));
        var (coordinator, session, editor, _) = _Flow(_ => new ScreenshotSelection
        {
            Region = new CaptureRect(100, 100, 400, 300),
            Marks = [box],
        });

        await coordinator.CaptureIntoAsync(session);

        Assert.Equal(new CaptureRect(100, 100, 400, 300), editor.Cropped);
        Assert.Equal(new[] { box }, editor.Burnt);
        Assert.Single(session.InjectedScreenshots);
    }

    /// <summary>
    /// Dismissing the surface injects nothing and says nothing. Escape is the ordinary way to change your mind,
    /// and it must not overwrite the region kept from a capture the operator actually took.
    /// </summary>
    [Fact]
    public async Task DismissingTheSurface_TakesNothingAndKeepsTheOldRegion()
    {
        var remembered = new CaptureRect(5, 5, 50, 50);
        var (coordinator, session, editor, settings) = _Flow(_ => null);
        await settings.SaveAsync(settings.Settings with { LastRegion = remembered });

        await coordinator.CaptureIntoAsync(session);

        Assert.Empty(session.InjectedScreenshots);
        Assert.Null(editor.Cropped);
        Assert.Equal(remembered, settings.Settings.LastRegion);
    }

    private static (ScreenshotCoordinator Coordinator, RecordingSession Session, FakeScreenshotImageEditor Editor, FakeScreenshotSettingsStore Settings) _Flow(
        Func<CaptureRect?, ScreenshotSelection?> pick)
    {
        var session = new RecordingSession();
        var cockpit = TestCockpit.NewViewModel();
        cockpit.SelectedSession = session;
        var editor = new FakeScreenshotImageEditor();
        var settings = new FakeScreenshotSettingsStore();

        var coordinator = new ScreenshotCoordinator(
            TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService()),
            new FakeScreenshotCapture { Result = Capture },
            cockpit,
            Substitute.For<IToastService>(),
            settings,
            editor,
            StubDesktopWindows.None,
            NullLogger<ScreenshotCoordinator>.Instance);
        coordinator.UseSelection((_, lastRegion) => Task.FromResult(pick(lastRegion)));

        return (coordinator, session, editor, settings);
    }
}
