using System.Globalization;
using Cockpit.App.Converters;
using FluentAssertions;

namespace Cockpit.Core.Tests.Converters;

/// <summary>
/// How a project card says when it was last worked on (AC-162). The card is read by someone deciding where to
/// carry on, so the answer is relative and in plain words — a timestamp answers a question they did not ask.
/// </summary>
public class LastOpenedConverterTests
{
    private static string Convert(DateTimeOffset? value) =>
        (string)LastOpenedConverter.Instance.Convert(value, typeof(string), null, CultureInfo.InvariantCulture)!;

    [Fact]
    public void AProjectNeverOpened_SaysSo_RatherThanShowingNothing()
    {
        Convert(null).Should().Be("Not opened yet");
    }

    [Theory]
    [InlineData(10, "Opened just now")]
    [InlineData(90, "Opened 1 min ago")]
    [InlineData(60 * 45, "Opened 45 min ago")]
    [InlineData(60 * 60, "Opened 1 hour ago")]
    [InlineData(60 * 60 * 5, "Opened 5 hours ago")]
    [InlineData(60 * 60 * 24, "Opened yesterday")]
    [InlineData(60 * 60 * 24 * 9, "Opened 9 days ago")]
    public void TheAgeIsRoundedToTheCoarsestUnitThatStillSaysSomething(int secondsAgo, string expected)
    {
        Convert(DateTimeOffset.Now.AddSeconds(-secondsAgo)).Should().Be(expected);
    }

    [Fact]
    public void AnOffsetOtherThanThisMachines_IsComparedAsTheInstantItStandsFor()
    {
        // The same moment, written in another zone's offset: subtracting DateTimeOffsets compares instants, so this
        // must not read as hours old just because the offset differs.
        var sameMomentElsewhere = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(-7));

        Convert(sameMomentElsewhere).Should().Be("Opened just now");
    }

    [Fact]
    public void AClockThatMovedBack_ReadsAsJustNow_RatherThanANegativeAge()
    {
        // A restored config or a corrected clock can leave a stamp in the future. "Opened -3 days ago" is nonsense
        // the operator would have to interpret.
        Convert(DateTimeOffset.Now.AddHours(3)).Should().Be("Opened just now");
    }
}
