using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.App.Controls;
using Material.Icons;

namespace Cockpit.App.Views;

// AC-2190: the one head, body and foot every Companion pop-out shares, after the companion-strook mockup's .ph/.pb/.pf:
// a 12.5px panel with a semibold title, a ghost close at the end of the head and a hairline over a quiet foot.
internal static class CompanionPanelChrome
{
    internal const double FontSize = 12.5;

    // The head: the title, whatever the panel puts beside it, and the close.
    public static Control Header(Control title, Action? close, params Control[] actions)
    {
        var head = new DockPanel { Margin = new Thickness(14, 10, 10, 8), LastChildFill = true };
        if (close is not null)
        {
            var button = IconButton(MaterialIconKind.Close, "Close", close);
            DockPanel.SetDock(button, Dock.Right);
            head.Children.Add(button);
        }

        foreach (var action in actions.Reverse())
        {
            DockPanel.SetDock(action, Dock.Right);
            action.Margin = new Thickness(0, 0, 4, 0);
            head.Children.Add(action);
        }

        title.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(title);

        return head;
    }

    public static Control Header(string title, Action? close, params Control[] actions) =>
        Header(Text(title, weight: FontWeight.SemiBold), close, actions);

    // The close of a panel that draws its own head: it closes the pop-out it sits in, and nothing else.
    public static Action CloseOf(Control panel) => () => (TopLevel.GetTopLevel(panel) as CompanionPopOut)?.Close();

    // The body's padding, under a head.
    public static Thickness BodyMargin => new(14, 0, 14, 12);

    // The foot: a hairline across, then 11px secondary text and whatever controls the panel ends on.
    public static Border Footer(params Control[] children)
    {
        var row = new DockPanel { LastChildFill = false };
        foreach (var child in children)
        {
            child.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(child);
        }

        var foot = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
        foot.Bind(Border.BorderBrushProperty, foot.GetResourceObservable("CockpitHairlineSoftBrush"));

        return foot;
    }

    public static TextBlock Text(string text, string brush = "CockpitTextPrimaryBrush", double size = FontSize, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brush));

        return block;
    }

    // A small heading over a group of rows (the mockup's .lbl).
    public static TextBlock Label(string text)
    {
        var label = Text(text, "CockpitTextFaintBrush", 11);
        label.Margin = new Thickness(0, 10, 0, 4);

        return label;
    }

    // The ghost icon button of a head: 24px, secondary ink.
    public static Button IconButton(MaterialIconKind kind, string name, Action click)
    {
        var button = new Button
        {
            Content = CockpitIcons.Icon(kind, 14),
            Classes = { "Ghost" },
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, button.GetResourceObservable("CockpitTextSecondaryBrush"));
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => click();

        return button;
    }

    // A pick row (the mockup's .rowb): icon, a name over a detail line, and a check when it is the one picked.
    public static Button PickRow(MaterialIconKind icon, string title, string detail, bool selected)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 9 };
        var glyph = CockpitIcons.Icon(icon, 15);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.Bind(Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, glyph.GetResourceObservable("CockpitTextSecondaryBrush"));
        row.Children.Add(glyph);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(title, weight: FontWeight.Medium));
        text.Children.Add(Text(detail, "CockpitTextSecondaryBrush", 11));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (selected)
        {
            var check = CockpitIcons.Icon(MaterialIconKind.Check, 14);
            check.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(check, 2);
            row.Children.Add(check);
        }

        var button = new Button
        {
            Content = row,
            Classes = { "Ghost", "companionPickRow" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(7),
        };
        button.Classes.Set("selected", selected);

        return button;
    }
}
