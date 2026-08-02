using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// Drives the "Current" badge in `MemorySourceLocationPickerDialog`'s list (AC-499): true when a row's own
// `Value` (the first binding) equals the picker's `CurrentValue` (the second, reached from the
// `DataTemplate` via `$parent[ListBox]`). Ordinal, matching the same comparison
// `ViewModels.MemorySourceLocationPickerViewModel` uses to pick the row on load — this converter only
// re-derives the same answer for display, it never sets `SelectedLocation` itself, so clicking a different
// row moves the selection highlight without moving this badge off the row the operator actually came in on.
public sealed class IsCurrentLocationConverter : IMultiValueConverter
{
    public static readonly IsCurrentLocationConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values is [string value, string current] && string.Equals(value, current, StringComparison.Ordinal);
}
