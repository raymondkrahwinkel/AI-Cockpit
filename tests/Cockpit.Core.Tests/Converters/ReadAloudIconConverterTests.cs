using System.Globalization;
using Cockpit.App.Converters;
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

        Assert.NotEqual(off, on);
        Assert.Equal(MaterialIconKind.VolumeHigh, on);
        Assert.Equal(MaterialIconKind.VolumeOff, off);
    }

    [Fact]
    public void Tip_SaysWhatTheStateIsAndWhatAClickDoes()
    {
        Assert.Contains("Click to stop", _ConvertTip(true));
        Assert.Contains("Click to start", _ConvertTip(false));
    }

    [Fact]
    public void Icon_WithNoBoundValueYet_FallsBackToTheOffFace()
    {
        Assert.Equal(_ConvertIcon(false), _ConvertIcon(null));
    }

    private static MaterialIconKind _ConvertIcon(bool? value) =>
        (MaterialIconKind)ReadAloudIconConverter.Icon.Convert(value, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture);

    private static string _ConvertTip(bool? value) =>
        (string)ReadAloudIconConverter.Tip.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);
}
