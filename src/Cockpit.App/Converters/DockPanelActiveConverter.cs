using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// Drives the dock rail highlight (AC-951); tabs persist when collapsed, so a null OpenDockPanelId must resolve
// false rather than fall through to null.
public sealed class DockPanelActiveConverter : IMultiValueConverter
{
    public static readonly DockPanelActiveConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [string id, var openId] && string.Equals(id, openId as string, StringComparison.Ordinal);
}
