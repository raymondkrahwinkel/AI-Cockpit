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
    // The two faces must differ, or the button says nothing. The null row is the view binding through a
    // coordinator that is not there (the Screenshotter, a test built without one): that has to land on the
    // off face, never on the open one.
    [Theory]
    [InlineData(true, MaterialIconKind.VolumeHigh)]
    [InlineData(false, MaterialIconKind.VolumeOff)]
    [InlineData(null, MaterialIconKind.VolumeOff)]
    public void Icon_ShowsTheStatesOwnFace_AndFallsBackToOffWhenNothingIsBound(bool? value, MaterialIconKind expected)
    {
        Assert.Equal(expected, _ConvertIcon(value));
    }

    [Fact]
    public void Tip_SaysWhatTheStateIsAndWhatAClickDoes()
    {
        Assert.Contains("Click to stop", _ConvertTip(true));
        Assert.Contains("Click to start", _ConvertTip(false));
    }

    private static MaterialIconKind _ConvertIcon(bool? value) =>
        (MaterialIconKind)ReadAloudIconConverter.Icon.Convert(value, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture);

    private static string _ConvertTip(bool? value) =>
        (string)ReadAloudIconConverter.Tip.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);
}
