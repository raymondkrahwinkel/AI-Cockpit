using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

public static class OptionsSearchMatcher
{
    public static bool MatchesAny(string? searchText, string keywords) =>
        string.IsNullOrWhiteSpace(searchText) || keywords.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase);
}

// Compares the selected category tag to the content tag without index arithmetic.
public sealed class CategoryTagEqualsConverter : IValueConverter
{
    public static readonly CategoryTagEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as ListBoxItem)?.Tag as string == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Category selection is display-only.");
}
