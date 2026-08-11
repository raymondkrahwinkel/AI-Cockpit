using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace Cockpit.App.Converters;

// The always-on listen toggle's face and its tooltip (AC-694): a microphone when the mic stays open, a struck-through
// one when only the held hotkey opens it. Same shape as ReadAloudIconConverter next to it — the icon says which way it
// is at a glance, the tooltip spells out the state and what clicking will do, since an icon alone is a guess.
public sealed class ListeningModeIconConverter : IValueConverter
{
    // The toggle's face — a `MaterialIconKind`, bound by the view to a nested `MaterialIcon`.
    public static readonly ListeningModeIconConverter Icon = new();

    // The toggle's tooltip — a different sentence per state, since an icon on its own does not say which way it is.
    public static readonly ListeningModeIconConverter Tip = new() { _isTooltip = true };

    private bool _isTooltip;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isAlwaysOn = value is true;

        if (!_isTooltip)
        {
            return isAlwaysOn ? MaterialIconKind.Microphone : MaterialIconKind.MicrophoneOff;
        }

        return isAlwaysOn
            ? "The microphone stays open and everything you say goes to the assistant. Click to stop listening."
            : "Only the held assistant hotkey opens the microphone. Click to keep it open (this costs money per utterance).";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The listening-mode icon is display-only; the toggle's command writes the state.");
}
