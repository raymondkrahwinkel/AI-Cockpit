using FluentAssertions;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// The platform that has no capture at all (AC-333). Null now means a read that produced no image, which the
/// coordinator passes over without a word because that is what a cancelled selection looks like — so a platform
/// that can never capture has to say something else, or an operator pressing the key gets silence.
/// </summary>
public class UnsupportedScreenshotCaptureTests
{
    [Fact]
    public void ItSaysUpFrontThatItCannotCapture()
    {
        new UnsupportedScreenshotCapture().IsSupported.Should().BeFalse();
    }

    [Fact]
    public async Task AskedAnyway_ItThrowsRatherThanLookingLikeACancel()
    {
        var capture = new UnsupportedScreenshotCapture();

        var act = async () => await capture.CaptureAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not supported on this platform*");
    }
}
