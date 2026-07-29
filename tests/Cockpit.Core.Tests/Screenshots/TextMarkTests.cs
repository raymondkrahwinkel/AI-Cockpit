using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>The typed note (AC-363): its plate, and what happens to it at the edge of the crop.</summary>
public class TextMarkTests
{
    private const uint Accent = 0xFF3B82F6;
    private const int Size = 28;

    /// <summary>
    /// The plate is the opposite shade of the letters, which is the whole legibility answer for this mark: ringing
    /// every glyph the way the arrow is ringed turns them to mud at the size a label is read at.
    /// </summary>
    [Theory]
    [InlineData(0xFF3B82F6, 0xFFFFFFFFu)]
    [InlineData(0xFFF4C150, 0xFF000000u)]
    public void ThePlateIsTheOppositeOfTheLetters(uint colour, uint expected)
    {
        Assert.Equal(expected, _Note(colour, 10, 10).Plate);
    }

    /// <summary>
    /// Moved into the crop's space and left whole. A note trimmed at the edge is a note that says something other
    /// than what was typed, which is worse than one that runs off the picture.
    /// </summary>
    [Fact]
    public void ANoteInsideTheRegion_ArrivesInTheCropsOwnCoordinates()
    {
        var clipped = _Note(Accent, 150, 180).ClipTo(new CaptureRect(100, 100, 500, 400));

        var textClipped = Assert.IsType<TextMark>(clipped);
        Assert.Equal(new CapturePoint(50, 80), textClipped.At);
    }

    /// <summary>A note whose corner is past the region is a note on something that is not being sent.</summary>
    [Fact]
    public void ANoteOutsideTheRegion_IsNotCarried()
    {
        Assert.Null(_Note(Accent, 700, 700).ClipTo(new CaptureRect(0, 0, 500, 500)));
    }

    /// <summary>
    /// One that starts inside and runs off the right-hand edge is kept. How wide it is depends on the font that
    /// draws it, which the mark cannot know — so what is asked is where it begins.
    /// </summary>
    [Fact]
    public void ANoteThatStartsInsideAndRunsOff_IsKept()
    {
        Assert.NotNull(_Note(Accent, 480, 200).ClipTo(new CaptureRect(0, 0, 500, 500)));
    }

    private static TextMark _Note(uint colour, int x, int y) =>
        new(new CapturePoint(x, y), "expected 12 here", colour, Size);
}
