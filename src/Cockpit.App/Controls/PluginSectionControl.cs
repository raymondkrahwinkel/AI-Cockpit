using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.App.Controls;

/// <summary>
/// A collapsible left-menu section for a plugin contribution (#14), built in code to match the cockpit
/// theme. It deliberately replaces Avalonia's <c>Expander</c>, whose header is an internal ToggleButton
/// that inherits the app's accent-on-checked style (a stray orange header). Here the header is a plain
/// themed row — secondary background, hairline border, a chevron and the title — that toggles the content.
/// </summary>
internal sealed class PluginSectionControl : UserControl
{
    private readonly TextBlock _chevron;
    private readonly Border _body;
    private bool _expanded = true;

    public PluginSectionControl(string title, Control content)
    {
        _chevron = new TextBlock
        {
            Text = "▾", // ▾
            Foreground = _Brush("CockpitTextFaintBrush", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = _Brush("CockpitTextPrimaryBrush", Brushes.White),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumn(_chevron, 1);
        headerGrid.Children.Add(titleBlock);
        headerGrid.Children.Add(_chevron);

        var header = new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush", Brushes.Transparent),
            BorderBrush = _Brush("CockpitHairlineBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = _Radius("CockpitControlRadius", 6),
            Padding = new Thickness(8, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = headerGrid,
        };
        header.PointerPressed += (_, _) => _Toggle();

        _body = new Border
        {
            Padding = new Thickness(8, 6, 8, 8),
            Child = content,
        };

        Content = new StackPanel
        {
            Spacing = 4,
            Children = { header, _body },
        };
    }

    private void _Toggle()
    {
        _expanded = !_expanded;
        _body.IsVisible = _expanded;
        _chevron.Text = _expanded ? "▾" : "▸"; // ▾ / ▸
    }

    private static IBrush _Brush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : fallback;

    private static CornerRadius _Radius(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius ? radius : new CornerRadius(fallback);
}
