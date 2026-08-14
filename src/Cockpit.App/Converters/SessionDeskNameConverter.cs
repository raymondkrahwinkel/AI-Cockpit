using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// AC-776: two bound values because the desk name lives in AssistantChatViewModel.DeskNameByPaneId, keyed by the
// row's PaneId — see the ticket for why.
public sealed class SessionDeskNameConverter : IMultiValueConverter
{
    public static readonly SessionDeskNameConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [string paneId, IReadOnlyDictionary<string, string> deskNames] ? Resolve(paneId, deskNames) : null;

    internal static string Resolve(string paneId, IReadOnlyDictionary<string, string> deskNames) =>
        deskNames.TryGetValue(paneId, out var name) ? name : "—";
}
