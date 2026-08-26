using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// Drives the "Current" badge (AC-499) with the ordinal comparison used to select the initial row. It only
// reflects that initial value, so changing the selection does not move the badge.
public sealed class IsCurrentLocationConverter : IMultiValueConverter
{
    public static readonly IsCurrentLocationConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values is [string value, string current] && string.Equals(value, current, StringComparison.Ordinal);
}
