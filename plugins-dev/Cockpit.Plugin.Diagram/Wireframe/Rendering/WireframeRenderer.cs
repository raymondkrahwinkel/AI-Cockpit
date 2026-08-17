using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Diagram.Wireframe.Model;
using static Cockpit.Plugin.Diagram.Wireframe.Rendering.WireframePalette;

namespace Cockpit.Plugin.Diagram.Wireframe.Rendering;

// Tree to controls (AC-871). Placement comes from Avalonia's own layout — Grid star-sizing for w:/h: weights,
// content sizing for everything else — so the source never carries a coordinate.
internal static class WireframeRenderer
{
    public static Control Render(WireframeNode node)
    {
        var control = _Build(node);
        WireframeSource.SetNode(control, node);

        if (node.Has(WireframeModifierName.Disabled))
        {
            control.Opacity = DisabledOpacity;
        }

        if (node.Alignment is { } alignment)
        {
            control.HorizontalAlignment = _Map(alignment);
        }

        return control;
    }

    private static Control _Build(WireframeNode node) => node.Kind switch
    {
        WireframeNodeKind.Screen => _Screen(node),
        WireframeNodeKind.Row => _Columns(node.Children),
        WireframeNodeKind.Column or WireframeNodeKind.Tab => _Rows(node.Children),
        WireframeNodeKind.Group => _Group(node),
        WireframeNodeKind.Tabs => _Tabs(node),
        WireframeNodeKind.Nav => _Nav(node),
        WireframeNodeKind.List => _List(node),
        WireframeNodeKind.Table => _Table(node),
        WireframeNodeKind.Item => _Item(node),
        WireframeNodeKind.Label => _Text(node.Text, TextSize, Ink),
        WireframeNodeKind.Button => _Button(node),
        WireframeNodeKind.Input => _Field(node, isSelect: false),
        WireframeNodeKind.Select => _Field(node, isSelect: true),
        WireframeNodeKind.Checkbox => _Toggle(node, isRadio: false),
        WireframeNodeKind.Radio => _Toggle(node, isRadio: true),
        WireframeNodeKind.Image => _Image(node),
        WireframeNodeKind.Divider => new Border { Height = 1, Background = Outline, Margin = new Thickness(0, 4) },
        _ => new Panel { MinHeight = Gap * 2 },
    };

    private static Control _Screen(WireframeNode node)
    {
        var header = new StackPanel
        {
            Spacing = Gap,
            Children =
            {
                _Text(node.Text ?? "Scherm", TitleSize, Ink),
                new Border { Height = 1, Background = Outline },
            },
        };

        return new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Pad + 4),
            Child = _Stack(header, _Rows(node.Children), fillsBody: true),
        };
    }

    private static Control _Group(WireframeNode node) => new Border
    {
        BorderBrush = Outline,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Radius),
        Padding = new Thickness(Pad),
        Child = node.Text is null
            ? _Rows(node.Children)
            : _Stack(_Text(node.Text, CaptionSize, Muted), _Rows(node.Children), fillsBody: false),
    };

    private static Control _Nav(WireframeNode node) => new Border
    {
        Background = Tint,
        BorderBrush = Outline,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Radius),
        Padding = new Thickness(6),
        Child = _Rows(node.Children, spacing: 2),
    };

    private static Control _Item(WireframeNode node)
    {
        var isSelected = node.Has(WireframeModifierName.Selected);
        return new Border
        {
            Background = isSelected ? Highlight : Brushes.Transparent,
            CornerRadius = new CornerRadius(Radius),
            Padding = new Thickness(10, 6),
            Child = _Text(node.Text, TextSize, isSelected ? Ink : Muted),
        };
    }

    private static Control _Button(WireframeNode node)
    {
        var isPrimary = node.Has(WireframeModifierName.Primary);
        return new Border
        {
            Background = isPrimary ? Solid : Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Padding = new Thickness(16, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _Text(node.Text, TextSize, isPrimary ? Paper : Ink),
        };
    }

    // Input and select are the same field with a different affordance: the label sits above the box, the value
    // inside it, and an empty box is exactly what "no value yet" looks like on paper.
    private static Control _Field(WireframeNode node, bool isSelect)
    {
        var value = node.ValueOf(WireframeModifierName.Value);
        var inside = new Grid();
        inside.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        inside.Children.Add(_Text(value, TextSize, value is null ? Muted : Ink));

        if (isSelect)
        {
            var chevron = _Text("▾", TextSize, Muted);
            inside.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(chevron, 1);
            inside.Children.Add(chevron);
        }

        var box = new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            MinHeight = ControlHeight,
            Padding = new Thickness(10, 6),
            Child = inside,
        };

        return node.Text is null ? box : _Stack(_Text(node.Text, CaptionSize, Muted), box, fillsBody: false, spacing: 4);
    }

    private static Control _Toggle(WireframeNode node, bool isRadio)
    {
        var mark = new Border
        {
            Width = 14,
            Height = 14,
            Background = node.Has(WireframeModifierName.Checked) ? Ink : Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(isRadio ? 7 : 2),
            VerticalAlignment = VerticalAlignment.Center,
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gap,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { mark, _Text(node.Text, TextSize, Ink) },
        };
    }

    // An empty list still has to read as a list, so one without item lines gets skeleton rows rather than an
    // empty box the eye skips over.
    private static Control _List(WireframeNode node)
    {
        var rows = node.Children.Count > 0
            ? _Rows(node.Children, spacing: 6)
            : _Skeleton(count: 3);

        var box = new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Padding = new Thickness(8),
            MinHeight = ControlHeight * 2,
            Child = rows,
        };

        return node.Text is null ? box : _Stack(_Text(node.Text, CaptionSize, Muted), box, fillsBody: true, spacing: 4);
    }

    // Item lines under a table are its column headings; without them the header band is drawn blank, which still
    // says "table" without inventing column names nobody asked for.
    private static Control _Table(WireframeNode node)
    {
        var columns = node.Children.Count > 0 ? node.Children.Count : 3;
        var heading = new Grid { ColumnSpacing = Gap, Background = Tint };
        for (var index = 0; index < columns; index++)
        {
            heading.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            if (index >= node.Children.Count)
            {
                continue;
            }

            var header = Render(node.Children[index]);
            Grid.SetColumn(header, index);
            heading.Children.Add(header);
        }

        heading.MinHeight = ControlHeight;

        var body = new Grid { ColumnSpacing = Gap, Margin = new Thickness(10, 8) };
        for (var index = 0; index < columns; index++)
        {
            body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            var cells = _Skeleton(count: 3);
            Grid.SetColumn(cells, index);
            body.Children.Add(cells);
        }

        var box = new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Child = _Stack(heading, body, fillsBody: true),
        };

        return node.Text is null ? box : _Stack(_Text(node.Text, CaptionSize, Muted), box, fillsBody: true, spacing: 4);
    }

    private static Control _Image(WireframeNode node)
    {
        var cross = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 1,1 M 1,0 L 0,1"),
            Stretch = Stretch.Fill,
            Stroke = Outline,
            StrokeThickness = 1,
        };

        var content = new Panel { Children = { cross } };
        if (node.Text is not null)
        {
            var caption = _Text(node.Text, CaptionSize, Muted);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(new Border
            {
                Background = Paper,
                Padding = new Thickness(6, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = caption,
            });
        }

        return new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            MinHeight = 90,
            Child = content,
        };
    }

    // A tab that is not open still gets rendered and still carries its node — hidden, not skipped, so no line of
    // the source is left without a control. The strip above it is chrome and deliberately carries none.
    private static Control _Tabs(WireframeNode node)
    {
        var tabs = node.Children.Where(child => child.Kind == WireframeNodeKind.Tab).ToList();
        var open = tabs.FirstOrDefault(tab => tab.Has(WireframeModifierName.Selected)) ?? tabs.FirstOrDefault();

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var bodies = new Panel();
        foreach (var tab in tabs)
        {
            var isOpen = ReferenceEquals(tab, open);
            strip.Children.Add(new Border
            {
                Background = isOpen ? Paper : Tint,
                BorderBrush = Outline,
                BorderThickness = new Thickness(1, 1, 1, isOpen ? 0 : 1),
                CornerRadius = new CornerRadius(Radius, Radius, 0, 0),
                Padding = new Thickness(14, 6),
                Child = _Text(tab.Text, TextSize, isOpen ? Ink : Muted),
            });

            var body = Render(tab);
            body.IsVisible = isOpen;
            bodies.Children.Add(body);
        }

        var panel = new Border
        {
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Pad),
            Child = bodies,
        };

        var loose = node.Children.Where(child => child.Kind != WireframeNodeKind.Tab).ToList();
        return loose.Count == 0
            ? _Stack(strip, panel, fillsBody: true, spacing: 0)
            : _Stack(strip, _Stack(panel, _Rows(loose), fillsBody: false), fillsBody: true, spacing: 0);
    }

    private static Grid _Rows(IReadOnlyList<WireframeNode> children, double spacing = Gap)
    {
        var grid = new Grid { RowSpacing = spacing };
        for (var index = 0; index < children.Count; index++)
        {
            var weight = children[index].WeightOf(WireframeModifierName.H);
            grid.RowDefinitions.Add(new RowDefinition(_Length(weight)));

            var control = Render(children[index]);
            Grid.SetRow(control, index);
            grid.Children.Add(control);
        }

        return grid;
    }

    private static Grid _Columns(IReadOnlyList<WireframeNode> children)
    {
        var grid = new Grid { ColumnSpacing = Gap };
        for (var index = 0; index < children.Count; index++)
        {
            var weight = children[index].WeightOf(WireframeModifierName.W);
            grid.ColumnDefinitions.Add(new ColumnDefinition(_Length(weight)));

            var control = Render(children[index]);
            Grid.SetColumn(control, index);
            grid.Children.Add(control);
        }

        return grid;
    }

    // A header over a body: the header takes what it needs, the body takes the rest when it is the thing that
    // should grow (a screen, a list) and its own height when it is not (a labelled field).
    private static Grid _Stack(Control header, Control body, bool fillsBody, double spacing = Gap)
    {
        var grid = new Grid { RowSpacing = spacing };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(fillsBody ? new GridLength(1, GridUnitType.Star) : GridLength.Auto));

        Grid.SetRow(body, 1);
        grid.Children.Add(header);
        grid.Children.Add(body);
        return grid;
    }

    private static Control _Skeleton(int count)
    {
        var stack = new StackPanel { Spacing = 8 };
        for (var index = 0; index < count; index++)
        {
            stack.Children.Add(new Border
            {
                Height = 8,
                Background = Skeleton,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, index % 2 == 0 ? 0 : 40, 0),
            });
        }

        return stack;
    }

    private static GridLength _Length(int? weight) =>
        weight is { } value ? new GridLength(value, GridUnitType.Star) : GridLength.Auto;

    private static TextBlock _Text(string? text, double size, IBrush brush) => new()
    {
        Text = text ?? string.Empty,
        FontSize = size,
        Foreground = brush,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static HorizontalAlignment _Map(WireframeAlignment alignment) => alignment switch
    {
        WireframeAlignment.Left => HorizontalAlignment.Left,
        WireframeAlignment.Center => HorizontalAlignment.Center,
        _ => HorizontalAlignment.Right,
    };
}
