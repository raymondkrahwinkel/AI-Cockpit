using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

/// <summary>
/// The read-aloud toggle's face and its tooltip (#73): a speaker when it is on, a struck-through speaker when it
/// is off. The session header is a strip, so the control says what it does with an icon rather than a word — but
/// an icon alone is a guess, hence the tooltip that spells out both the state and what clicking will do.
/// </summary>
public sealed class ReadAloudIconConverter : IValueConverter
{
    /// <summary>The toggle's face.</summary>
    public static readonly ReadAloudIconConverter Icon = new();

    /// <summary>The toggle's tooltip — a different sentence per state, since an icon on its own does not say which way it is.</summary>
    public static readonly ReadAloudIconConverter Tip = new() { _isTooltip = true };

    private bool _isTooltip;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isOn = value is true;

        if (!_isTooltip)
        {
            return isOn ? "🔊" : "🔇";
        }

        return isOn
            ? "Reading each reply aloud. Click to stop."
            : "Read each reply aloud, sentence by sentence (the voice model downloads on first use). Click to start.";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The read-aloud icon is display-only; the toggle writes the state itself.");
}
