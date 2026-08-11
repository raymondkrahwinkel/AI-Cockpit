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
    [Fact]
    public void Icon_DiffersBetweenAlwaysOnAndOff()
    {
        var on = _ConvertIcon(true);
        var off = _ConvertIcon(false);

        Assert.NotEqual(off, on);
        Assert.Equal(MaterialIconKind.Microphone, on);
        Assert.Equal(MaterialIconKind.MicrophoneOff, off);
    }

    [Fact]
    public void Tip_SaysWhatTheStateIsAndWhatAClickDoes()
    {
        Assert.Contains("Click to stop listening", _ConvertTip(true));
        Assert.Contains("Click to keep it open", _ConvertTip(false));
    }

    [Fact]
    public void Icon_WithNoBoundValueYet_FallsBackToTheOffFace()
    {
        // The view binds through `Indicator`, which is null in the Screenshotter and in tests built without a
        // coordinator — that has to land on "not listening", never on the open-mic face.
        Assert.Equal(_ConvertIcon(false), _ConvertIcon(null));
    }

    private static MaterialIconKind _ConvertIcon(bool? value) =>
        (MaterialIconKind)ListeningModeIconConverter.Icon.Convert(value, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture);

    private static string _ConvertTip(bool? value) =>
        (string)ListeningModeIconConverter.Tip.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);
}
