using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// Omits a resource row's bottom border when it is last (AC-485). CSS selectors cannot cross Avalonia's item
// presenter boundary, and a MultiBinding over the stable collection reference went stale after mutations; the
// dialog therefore updates IsLastRow explicitly and this converter only reads it.
public sealed class LastResourceRowBorderThicknessConverter : IValueConverter
{
    public static readonly LastResourceRowBorderThicknessConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new Thickness(0) : new Thickness(0, 0, 0, 1);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
