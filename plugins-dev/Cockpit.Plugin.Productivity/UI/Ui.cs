using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.UI;

internal static class Ui
{
    public static T Themed<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    public static Border Card(Control child, double padding = 16) =>
        new Border
        {
            Child = child,
            Padding = new Thickness(padding),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        }.Themed(Border.BorderBrushProperty, "CockpitHairlineBrush")
            .Themed(Border.BackgroundProperty, "CockpitPanelBgBrush");

    public static TextBlock Hint(string text) =>
        new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }
            .Themed(TextBlock.ForegroundProperty, "CockpitTextSecondaryBrush");

    public static Border Tag(string text, string brush = "CockpitTextSecondaryBrush") =>
        new Border
        {
            Classes = { "tag" },
            Child = new TextBlock { Text = text, FontSize = 11 }.Themed(TextBlock.ForegroundProperty, brush),
        };

    public static string Duration(TimeSpan duration)
    {
        var minutes = (long)duration.TotalMinutes;
        return $"{minutes / 60}u{minutes % 60:00}";
    }

    public static string Source(CockpitTimeEntrySource source) =>
        source == CockpitTimeEntrySource.Stopwatch ? "stopwatch" : "duur";

    public static string Status(CockpitTimeEntry entry) => entry.Status switch
    {
        CockpitTimeEntryStatus.Open => "open",
        CockpitTimeEntryStatus.Offered => $"aangeboden op {entry.OfferedAt?.ToLocalTime():d MMM}",
        _ => "geboekt",
    };
}
