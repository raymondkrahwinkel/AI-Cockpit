using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// AC-1507: one marking for any Options field a manifest locks. Bound to the view model's managed-paths map, with the
// field's cockpit.json path as the parameter: `Editable` for IsEnabled, `Label` for the "Managed by …" line.
public sealed class ManagedSettingConverter(bool label) : IValueConverter
{
    public static readonly ManagedSettingConverter Editable = new(false);

    public static readonly ManagedSettingConverter Label = new(true);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var by = value is IReadOnlyDictionary<string, string> managed && parameter is string path ? managed.GetValueOrDefault(path) : null;
        return label ? (by is null ? null : $"Managed by {by}") : by is null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
