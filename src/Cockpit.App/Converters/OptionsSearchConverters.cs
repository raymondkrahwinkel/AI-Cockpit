using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// AC-1000: the Options sidebar's live search. Rows and categories carry their own keyword strings as
// ConverterParameter rather than new view-model properties per field — a search over ~90 static rows does not
// need a per-row bindable property, just a string to match against.
public static class OptionsSearchMatcher
{
    // Empty search = nothing filtered. Otherwise a plain case-insensitive substring match — no per-word tokenising,
    // the mockup's own examples ("session", "hotkey") are single terms.
    public static bool MatchesAny(string? searchText, string keywords) =>
        string.IsNullOrWhiteSpace(searchText) || keywords.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase);

    // groupsPipeSeparated: one category's row keyword-groups joined with "||". Empty search returns 0 so the
    // match-count badge hides outside of an active search, even though every row is still shown.
    public static int CountMatches(string? searchText, string groupsPipeSeparated)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return 0;
        }

        return groupsPipeSeparated.Split("||", StringSplitOptions.RemoveEmptyEntries)
            .Count(group => MatchesAny(searchText, group));
    }
}

public sealed class OptionsRowVisibleConverter : IValueConverter
{
    public static readonly OptionsRowVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        OptionsSearchMatcher.MatchesAny(value as string, parameter as string ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Search-filtered row visibility is display-only.");
}

public sealed class OptionsCategoryVisibleConverter : IValueConverter
{
    public static readonly OptionsCategoryVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var searchText = value as string;
        var groups = parameter as string ?? string.Empty;
        return string.IsNullOrWhiteSpace(searchText) || OptionsSearchMatcher.CountMatches(searchText, groups) > 0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Search-filtered category visibility is display-only.");
}

public sealed class OptionsCategoryMatchCountConverter : IValueConverter
{
    public static readonly OptionsCategoryMatchCountConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        OptionsSearchMatcher.CountMatches(value as string, parameter as string ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The match-count badge is display-only.");
}

// Switches which of the 12 stacked category ScrollViewers is visible, by comparing the nav ListBox's
// SelectedItem.Tag against this ScrollViewer's own Tag — avoids index arithmetic against the 3 non-selectable
// group-header items sharing the same ListBox.
public sealed class CategoryTagEqualsConverter : IValueConverter
{
    public static readonly CategoryTagEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as ListBoxItem)?.Tag as string == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Category selection is display-only.");
}
