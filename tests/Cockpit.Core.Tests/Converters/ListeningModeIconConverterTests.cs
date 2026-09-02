using System.Globalization;
using Cockpit.App.Converters;
using Material.Icons;

namespace Cockpit.Core.Tests.Converters;

/// <summary>
/// <see cref="ListeningModeIconConverter"/> (AC-694): the always-on listen toggle went from a switch track to an icon
/// button, so the icon has to differ per state — and the tooltip has to say which state that is, because an icon on
/// its own does not.
/// </summary>
public class ListeningModeIconConverterTests
{
    // The two faces must differ, or the button says nothing. The null row is the view binding through a
    // coordinator that is not there (the Screenshotter, a test built without one): that has to land on the
    // off face, never on the open one.
    [Theory]
    [InlineData(true, MaterialIconKind.Microphone)]
    [InlineData(false, MaterialIconKind.MicrophoneOff)]
    [InlineData(null, MaterialIconKind.MicrophoneOff)]
    public void Icon_ShowsTheStatesOwnFace_AndFallsBackToOffWhenNothingIsBound(bool? value, MaterialIconKind expected)
    {
        Assert.Equal(expected, _ConvertIcon(value));
    }

    [Fact]
    public void Tip_SaysWhatTheStateIsAndWhatAClickDoes()
    {
        Assert.Contains("Click to stop listening", _ConvertTip(true));
        Assert.Contains("Click to keep it open", _ConvertTip(false));
    }

    private static MaterialIconKind _ConvertIcon(bool? value) =>
        (MaterialIconKind)ListeningModeIconConverter.Icon.Convert(value, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture);

    private static string _ConvertTip(bool? value) =>
        (string)ListeningModeIconConverter.Tip.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);
}
