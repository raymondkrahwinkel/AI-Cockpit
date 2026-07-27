using FluentAssertions;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// Reconciling the desktop's display list with the one image the Linux portal hands back (AC-326). The portal
/// says nothing about what went into that image, so this is the only thing standing between a display list that
/// describes a different desktop and a selection UI cropping the wrong region with nothing looking amiss.
/// </summary>
public class ComposedCaptureLayoutTests
{
    /// <summary>One display at 100%: the image is the display, and the two coordinate spaces coincide.</summary>
    [Fact]
    public void OneUnscaledDisplay_FillsTheImage()
    {
        var layout = ComposedCaptureLayout.TryCompose([_Display(0, 0, 1920, 1080)], 1920, 1080);

        layout.Should().ContainSingle();
        layout![0].ImageBounds.Should().Be(new CaptureRect(0, 0, 1920, 1080));
        layout[0].DesktopBounds.Should().Be(new CaptureRect(0, 0, 1920, 1080));
    }

    /// <summary>
    /// A single fractionally-scaled panel — the laptop this is being built on. The compositor renders 1920×1080
    /// of desktop into 2880×1620 pixels, and one display is trivially its own bounding box, so the whole image
    /// belongs to it whatever the factor.
    /// </summary>
    [Fact]
    public void OneScaledDisplay_TakesTheWholeImage_AtWhateverFactorTheCompositorUsed()
    {
        var layout = ComposedCaptureLayout.TryCompose([_Display(0, 0, 1920, 1080, scale: 1.5)], 2880, 1620);

        layout.Should().ContainSingle();
        layout![0].ImageBounds.Should().Be(new CaptureRect(0, 0, 2880, 1620));
        layout[0].Scale.Should().Be(1.5, "callers that size something in a display's own pixels have no other way to ask");
    }

    /// <summary>
    /// Two displays side by side, composed at one scale. The second starts at desktop x = 1920 and at image
    /// x = 2880 — the number a caller cannot derive and the reason the layout travels with the pixels at all.
    /// </summary>
    [Fact]
    public void TwoDisplaysAtOneScale_TileTheImageInOrder()
    {
        var layout = ComposedCaptureLayout.TryCompose(
            [_Display(0, 0, 1920, 1080, scale: 1.5), _Display(1920, 0, 1920, 1080, scale: 1.5)],
            5760,
            1620);

        layout.Should().HaveCount(2);
        layout![0].ImageBounds.Should().Be(new CaptureRect(0, 0, 2880, 1620));
        layout[1].ImageBounds.Should().Be(new CaptureRect(2880, 0, 2880, 1620));
    }

    /// <summary>
    /// Adjacent displays must stay adjacent in the image: both edges are scaled and only then subtracted, so a
    /// fractional ratio cannot open a seam between them that nothing owns. Red if the width is scaled instead of
    /// the far edge — 1001 × 1.5 rounds each display to 1502 columns, which is 3004 for an image 3003 wide.
    /// </summary>
    [Fact]
    public void DisplaysLaidEdgeToEdge_LeaveNoGapInTheImage()
    {
        var layout = ComposedCaptureLayout.TryCompose(
            [_Display(0, 0, 1001, 1000), _Display(1001, 0, 1001, 1000)],
            3003,
            1500);

        layout.Should().HaveCount(2);
        layout![0].ImageBounds.Right.Should().Be(layout[1].ImageBounds.X);
        (layout[0].ImageBounds.Width + layout[1].ImageBounds.Width).Should().Be(3003);
    }

    /// <summary>
    /// The refusal the ticket exists for. An image that is not the size the display list implies means one of
    /// the two describes a different desktop — cropping by that layout would take the wrong region, silently.
    /// </summary>
    [Fact]
    public void AnImageThatIsNotTheSizeTheDisplaysImply_IsRefused()
    {
        var layout = ComposedCaptureLayout.TryCompose([_Display(0, 0, 1920, 1080)], 1920, 1200);

        layout.Should().BeNull();
    }

    /// <summary>
    /// The multi-monitor case this cannot serve: two displays composed at their own scales rather than one — the
    /// shape Windows and macOS produce — is not what the portal does, and guessing which of the two it was would
    /// be a coin toss over which half of the image is wrong.
    /// </summary>
    [Fact]
    public void DisplaysComposedAtDifferentScales_AreRefusedRatherThanGuessedAt()
    {
        var layout = ComposedCaptureLayout.TryCompose(
            [_Display(0, 0, 1920, 1080, scale: 1.5), _Display(1920, 0, 1920, 1080)],
            4800,
            1620);

        layout.Should().BeNull();
    }

    /// <summary>
    /// One pixel of slack and no more: an odd desktop height at 150% cannot come out whole, and the compositor
    /// rounds it. Two pixels out is not rounding.
    /// </summary>
    [Theory]
    [InlineData(1621, true)]
    [InlineData(1622, false)]
    public void RoundingCostsAtMostAPixel(int imageHeight, bool accepted)
    {
        var layout = ComposedCaptureLayout.TryCompose([_Display(0, 0, 1920, 1080, scale: 1.5)], 2880, imageHeight);

        (layout is not null).Should().Be(accepted);
    }

    /// <summary>
    /// An image smaller than the layout is not a scale — no desktop renders a display below its own resolution,
    /// so this is a display list for a desktop other than the one that was captured.
    /// </summary>
    [Fact]
    public void AnImageSmallerThanTheDesktop_IsRefused()
    {
        var layout = ComposedCaptureLayout.TryCompose([_Display(0, 0, 1920, 1080)], 960, 540);

        layout.Should().BeNull();
    }

    [Fact]
    public void NoDisplays_ComposeToNothing()
    {
        ComposedCaptureLayout.TryCompose([], 1920, 1080).Should().BeNull();
    }

    /// <summary>
    /// A display with no extent, hiding behind a bounding box the image does match — the case the size checks
    /// cannot see, since a zero-width display sitting against the right edge changes neither. Letting it through
    /// puts a <see cref="CapturedDisplay"/> into the layout whose own mapping divides by its width.
    /// </summary>
    [Fact]
    public void ADisplayWithNoExtent_IsRefused_EvenWhenTheImageStillFitsTheRest()
    {
        var layout = ComposedCaptureLayout.TryCompose(
            [_Display(0, 0, 1920, 1080), _Display(1920, 0, 0, 1080)],
            1920,
            1080);

        layout.Should().BeNull();
    }

    /// <summary>
    /// A desktop whose origin is not (0,0) — a second monitor placed to the left puts the first at a positive x
    /// and the second at a negative one. The image starts at its own origin regardless.
    /// </summary>
    [Fact]
    public void ADesktopExtendingLeftOfTheOrigin_IsPlacedFromTheImagesOwnCorner()
    {
        var layout = ComposedCaptureLayout.TryCompose(
            [_Display(-1920, 0, 1920, 1080), _Display(0, 0, 1920, 1080)],
            3840,
            1080);

        layout.Should().HaveCount(2);
        layout![0].ImageBounds.Should().Be(new CaptureRect(0, 0, 1920, 1080));
        layout[1].ImageBounds.Should().Be(new CaptureRect(1920, 0, 1920, 1080));
    }

    private static DesktopDisplay _Display(int x, int y, int width, int height, double scale = 1.0) =>
        new() { Bounds = new CaptureRect(x, y, width, height), Scale = scale };
}
