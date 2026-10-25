using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Material.Icons;
using Material.Icons.Avalonia;

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

    public static Border Pill(string text, string foreground, string background) =>
        new Border
        {
            Padding = new Thickness(8, 2),
            CornerRadius = new CornerRadius(12),
            Child = new TextBlock { Text = text, FontSize = 10.5 }.Themed(TextBlock.ForegroundProperty, foreground),
        }.Themed(Border.BackgroundProperty, background);

    public static Ellipse Dot(string brush, double size = 8) =>
        new Ellipse { Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
            .Themed(Shape.FillProperty, brush);

    public static Button IconButton(string name, MaterialIconKind kind, string tooltip)
    {
        var button = new Button
        {
            Name = name,
            Content = new MaterialIcon { Kind = kind, Width = 13, Height = 13 },
            Padding = new Thickness(5, 2),
            Classes = { "Subtle", "Compact" },
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

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
