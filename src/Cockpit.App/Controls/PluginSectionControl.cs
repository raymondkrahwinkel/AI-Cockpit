using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Controls;

/// <summary>
/// A collapsible left-menu section for a plugin contribution (#14), built in code to match the cockpit
/// theme. It deliberately replaces Avalonia's <c>Expander</c>, whose header is an internal ToggleButton
/// that inherits the app's accent-on-checked style (a stray orange header). Here the header is a plain
/// themed row — secondary background, hairline border, a chevron and the title — that toggles the content.
/// </summary>
internal sealed class PluginSectionControl : UserControl
{
    private readonly MaterialIcon _chevron;
    private readonly Border _body;
    private bool _expanded = true;

    /// <param name="onSettings">
    /// When given, a gear in the section's header runs it — the same short way into a plugin's settings that its
    /// left-menu button has, for the plugins that contribute a section instead of a button.
    /// </param>
    public PluginSectionControl(string title, Control content, Action? onSettings = null)
    {
        _chevron = CockpitIcons.Icon(MaterialIconKind.ChevronDown, 13);
        _chevron.Foreground = _Brush("CockpitTextFaintBrush", Brushes.Gray);
        _chevron.VerticalAlignment = VerticalAlignment.Center;

        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = _Brush("CockpitTextPrimaryBrush", Brushes.White),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumn(_chevron, 2);
        headerGrid.Children.Add(titleBlock);
        headerGrid.Children.Add(_chevron);

        if (onSettings is not null)
        {
            var gear = new Button
            {
                Content = CockpitIcons.Gear(),
                Classes = { "Subtle" },
                Padding = new Thickness(4, 2),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(gear, $"{title} settings");
            gear.Click += (_, _) => onSettings();
            Grid.SetColumn(gear, 1);
            headerGrid.Children.Add(gear);
        }

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
        // Pressing the header collapses the section — except on the gear, which is a button in that header and owns
        // its own press. Same rule as the window chrome's caption buttons.
        header.PointerPressed += (_, e) =>
        {
            if (e.Source is Button)
            {
                return;
            }

            _Toggle();
        };

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
        _chevron.Kind = _expanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;
    }

    private static IBrush _Brush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : fallback;

    private static CornerRadius _Radius(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius ? radius : new CornerRadius(fallback);
}
