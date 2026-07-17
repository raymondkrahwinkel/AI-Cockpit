using System.Globalization;
using Cockpit.App.Converters;
using FluentAssertions;
using Material.Icons;

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
        var on = _ConvertIcon(true);
        var off = _ConvertIcon(false);

        on.Should().NotBe(off);
        on.Should().Be(MaterialIconKind.VolumeHigh);
        off.Should().Be(MaterialIconKind.VolumeOff);
    }

    [Fact]
    public void Tip_SaysWhatTheStateIsAndWhatAClickDoes()
    {
        _ConvertTip(true).Should().Contain("Click to stop");
        _ConvertTip(false).Should().Contain("Click to start");
    }

    [Fact]
    public void Icon_WithNoBoundValueYet_FallsBackToTheOffFace()
    {
        _ConvertIcon(null).Should().Be(_ConvertIcon(false));
    }

    private static MaterialIconKind _ConvertIcon(bool? value) =>
        (MaterialIconKind)ReadAloudIconConverter.Icon.Convert(value, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture);

    private static string _ConvertTip(bool? value) =>
        (string)ReadAloudIconConverter.Tip.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);
}
