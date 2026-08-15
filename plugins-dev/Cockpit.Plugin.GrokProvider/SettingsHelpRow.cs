using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.Plugin.GrokProvider;

// Wraps a settings input in a row with a small "?" help tooltip — mirrors the sibling plugins' own copy;
// plugins cannot share code with each other, only with the host, so each keeps its own.
internal static class SettingsHelpRow
{
    public static Control Build(Control input, string helpText)
    {
        var help = new TextBlock
        {
            Text = "?",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Opacity = 0.65,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Help),
        };
        ToolTip.SetTip(help, helpText);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(input, 0);
        Grid.SetColumn(help, 1);
        row.Children.Add(input);
        row.Children.Add(help);
        return row;
    }
}
