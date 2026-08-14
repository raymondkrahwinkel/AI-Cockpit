using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// AC-776: a session's desk, for the session-status pill's flyout list and tooltip. Two bound values because the
// name lives in `AssistantChatViewModel.DeskNameByPaneId` (resolved once per rebuild, see that property's remarks)
// while the row itself is a `SessionPanelViewModel`, which only carries the raw `PaneId` — a plain single-value
// converter has no way to reach the dictionary.
public sealed class SessionDeskNameConverter : IMultiValueConverter
{
    public static readonly SessionDeskNameConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [string paneId, IReadOnlyDictionary<string, string> deskNames] ? Resolve(paneId, deskNames) : null;

    internal static string Resolve(string paneId, IReadOnlyDictionary<string, string> deskNames) =>
        deskNames.TryGetValue(paneId, out var name) ? name : "—";
}
