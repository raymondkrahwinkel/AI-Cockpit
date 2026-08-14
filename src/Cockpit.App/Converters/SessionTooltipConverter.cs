using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// AC-776: the session-status pill segment's hover tooltip (criterion 6) — the full session name (the pill itself
// trims it), the status and desk, and the statusline, with the statusline row dropped entirely rather than shown
// blank when the session has not set one.
public sealed class SessionTooltipConverter : IMultiValueConverter
{
    public static readonly SessionTooltipConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [string title, string statusLabel, string paneId, var statuslineValue, IReadOnlyDictionary<string, string> deskNames])
        {
            return null;
        }

        var desk = SessionDeskNameConverter.Resolve(paneId, deskNames);
        var text = $"{title}\n{statusLabel} · {desk}";
        return statuslineValue is string { Length: > 0 } statusline ? $"{text}\n{statusline}" : text;
    }
}
