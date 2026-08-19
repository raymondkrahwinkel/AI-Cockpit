using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// Drives the dock rail's active-tab highlight (AC-951): true when a tab's own Id (the first binding) equals
// OpenDockPanelId (the second, reached from the ItemsControl via $parent). The second value is nullable — the
// rail's tabs stay on screen while collapsed, when OpenDockPanelId is null and nothing should highlight — so,
// unlike IsCurrentLocationConverter, this always resolves to a bool rather than falling through to null.
public sealed class DockPanelActiveConverter : IMultiValueConverter
{
    public static readonly DockPanelActiveConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [string id, var openId] && string.Equals(id, openId as string, StringComparison.Ordinal);
}
