using System.Globalization;
using Cockpit.App.Converters;
using FluentAssertions;

namespace Cockpit.Core.Tests.Converters;

/// <summary>
/// <see cref="ReadAloudIconConverter"/> (#73): the read-aloud toggle went from a word to a speaker, so the icon
/// has to differ per state — and the tooltip has to say which state that is, because an icon on its own does not.
/// </summary>
public class ReadAloudIconConverterTests
{
    [Fact]
    public void Icon_DiffersBetweenOnAndOff()
    {
        var on = _Convert(ReadAloudIconConverter.Icon, true);
        var off = _Convert(ReadAloudIconConverter.Icon, false);

        on.Should().NotBe(off);
        on.Should().NotBeEmpty();
        off.Should().NotBeEmpty();
    }

    [Fact]
    public void Tip_SaysWhatTheStateIsAndWhatAClickDoes()
    {
        _Convert(ReadAloudIconConverter.Tip, true).Should().Contain("Click to stop");
        _Convert(ReadAloudIconConverter.Tip, false).Should().Contain("Click to start");
    }

    [Fact]
    public void Icon_WithNoBoundValueYet_FallsBackToTheOffFace()
    {
        _Convert(ReadAloudIconConverter.Icon, null).Should().Be(_Convert(ReadAloudIconConverter.Icon, false));
    }

    private static string _Convert(ReadAloudIconConverter converter, bool? value) =>
        (string)converter.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);
}
