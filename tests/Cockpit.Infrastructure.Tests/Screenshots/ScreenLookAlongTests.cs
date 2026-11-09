using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Screenshots;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Screenshots;

// AC-2209: a capture of the whole screen without the operator's click is a leak (passwords, mail, other clients)
// that shows up nowhere else, so the screen is read only inside a session the card started.
public sealed class ScreenLookAlongTests
{
    [Fact]
    public async Task TheScreenIsRead_OnlyInsideASessionTheOperatorStarted_NeverWithoutAClickAfterStopOrAfterTheTimer()
    {
        var display = new CaptureRect(0, 0, 1920, 1080);
        var capture = Substitute.For<IScreenshotCapture>();
        capture.CaptureAsync(Arg.Any<CancellationToken>()).Returns(new ScreenCapture
        {
            Image = [1],
            Displays = [new CapturedDisplay { DesktopBounds = display, Scale = 1, ImageBounds = display }],
        });
        var displays = Substitute.For<IDesktopDisplays>();
        displays.EnumerateAsync(Arg.Any<CancellationToken>()).Returns([new DesktopDisplay { Bounds = display, Scale = 1 }]);
        var editor = Substitute.For<IScreenshotImageEditor>();
        editor.Crop(Arg.Any<byte[]>(), Arg.Any<CaptureRect>()).Returns([2]);
        var approve = false;
        var consent = Substitute.For<IConsentBroker>();
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(_ => approve
            ? new ConsentDecision(ConsentOutcome.Approved) { Chosen = new Dictionary<string, IReadOnlyList<string>> { ["display"] = ["0"], ["minutes"] = ["10"] } }
            : ConsentDecision.Denied);
        var clock = new _Clock();
        var lookAlong = new ScreenLookAlong(capture, displays, editor, consent, clock);

        Assert.Null((await lookAlong.LookAsync("Without a click")).Png);
        approve = true;
        Assert.Null((await lookAlong.LookAsync("The click starts it")).Png);
        Assert.NotNull((await lookAlong.LookAsync("One look inside it")).Png);
        lookAlong.Stop();
        Assert.Null((await lookAlong.LookAsync("After Stop")).Png);
        clock.Now += TimeSpan.FromMinutes(10);
        Assert.Null((await lookAlong.LookAsync("After the timer")).Png);

        await capture.Received(1).CaptureAsync(Arg.Any<CancellationToken>());
        await consent.Received(4).RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>());
    }

    private sealed class _Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
