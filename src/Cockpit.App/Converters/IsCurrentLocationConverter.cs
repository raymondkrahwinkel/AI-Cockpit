using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

/// <summary>
/// Drives the "Current" badge in <c>MemorySourceLocationPickerDialog</c>'s list (AC-499): true when a row's own
/// <c>Value</c> (the first binding) equals the picker's <c>CurrentValue</c> (the second, reached from the
/// <c>DataTemplate</c> via <c>$parent[ListBox]</c>). Ordinal, matching the same comparison
/// <see cref="ViewModels.MemorySourceLocationPickerViewModel"/> uses to pick the row on load — this converter only
/// re-derives the same answer for display, it never sets <c>SelectedLocation</c> itself, so clicking a different
/// row moves the selection highlight without moving this badge off the row the operator actually came in on.
/// </summary>
public sealed class IsCurrentLocationConverter : IMultiValueConverter
{
    public static readonly IsCurrentLocationConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values is [string value, string current] && string.Equals(value, current, StringComparison.Ordinal);
}
