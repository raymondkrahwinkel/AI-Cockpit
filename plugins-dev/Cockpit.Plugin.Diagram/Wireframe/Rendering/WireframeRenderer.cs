using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Core.Wireframe.Model;
using static Cockpit.Plugin.Diagram.Wireframe.Rendering.WireframePalette;

namespace Cockpit.Plugin.Diagram.Wireframe.Rendering;

// Tree to controls (AC-871). Placement comes from Avalonia's own layout — Grid star-sizing for w:/h: weights,
// content sizing for everything else — so the source never carries a coordinate.
internal static class WireframeRenderer
{
    // The design canvas one screen is drawn on — wide enough that a desktop layout needs zoom/pan to see at once
    // (AC-837). A wireframe's Grid star-sizing has no natural size of its own, so it is handed one. This is also
    // what a document with no `viewport` line renders at (AC-915) — the size nothing here has ever changed.
    public static readonly Size ScreenSize = new(960, 640);

    private static readonly Size TabletSize = new(768, 1024);
    private static readonly Size MobileSize = new(390, 844);

    private const double BoardGap = 48;
    private const double BoardCaption = 28;

    // AC-915: the three sheet sizes the wireframe format has words for. Kept here rather than on the enum itself —
    // Core knows nothing about Avalonia's Size.
    public static Size SizeOf(WireframeViewport? viewport) => viewport switch
    {
        WireframeViewport.Tablet => TabletSize,
        WireframeViewport.Mobile => MobileSize,
        _ => ScreenSize,
    };

    // AC-901: the whole document at once — every screen as a board of its own, in a near-square grid so a document
    // of eight screens is still something you can take in rather than one endless row.
    public static Control Overview(IReadOnlyList<WireframeNode> screens, Size screen)
    {
        var size = OverviewSize(screens.Count, screen);
        // The desk the boards lie on, tinted so a white screen reads as a sheet on it — and so the names above them
        // are drawn onto something rather than onto nothing, which is where text comes out doubled.
        var canvas = new Canvas { Width = size.Width, Height = size.Height, Background = Tint };
        for (var index = 0; index < screens.Count; index++)
        {
            var bounds = BoardBounds(index, screens.Count, screen);
            var board = new Panel { Width = bounds.Width, Height = bounds.Height, Children = { Render(screens[index]) } };
            Canvas.SetLeft(board, bounds.X);
            Canvas.SetTop(board, bounds.Y);
            canvas.Children.Add(board);

            // The board goes in first: the selection mark looks up the control a node was drawn as, and it is the
            // board that should carry it rather than the words above it.
            var caption = _Text(screens[index].Text ?? "Screen", TitleSize, Muted);
            WireframeSource.SetNode(caption, screens[index]);
            Canvas.SetLeft(caption, bounds.X);
            Canvas.SetTop(caption, bounds.Y - BoardCaption);
            canvas.Children.Add(caption);
        }

        return canvas;
    }

    public static int OverviewColumns(int screens) => Math.Max(1, (int)Math.Ceiling(Math.Sqrt(screens)));

    // AC-915: no default on `screen` — every caller has to say which viewport it is drawing, so a missed one is a
    // compiler error rather than an arrow or a board landing on the wrong rectangle (see _DrawFlowArrows).
    public static Size OverviewSize(int screens, Size screen)
    {
        var columns = OverviewColumns(screens);
        var rows = Math.Max(1, (int)Math.Ceiling(screens / (double)columns));
        return new Size(
            BoardGap + columns * (screen.Width + BoardGap),
            BoardGap + rows * (screen.Height + BoardCaption + BoardGap));
    }

    // Where one board sits on the overview canvas, its caption in the strip of room straight above it.
    public static Rect BoardBounds(int index, int screens, Size screen)
    {
        var columns = OverviewColumns(screens);
        return new Rect(
            new Point(
                BoardGap + index % columns * (screen.Width + BoardGap),
                BoardGap + BoardCaption + index / columns * (screen.Height + BoardCaption + BoardGap)),
            screen);
    }

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
        WireframeNodeKind.Column or WireframeNodeKind.Tab or WireframeNodeKind.Main => _Rows(node.Children),
        WireframeNodeKind.Group => _Group(node),
        WireframeNodeKind.Header => _Band(node, new Thickness(0, 0, 0, 1), TitleSize),
        WireframeNodeKind.Footer => _Band(node, new Thickness(0, 1, 0, 0), CaptionSize),
        WireframeNodeKind.Sidebar => _Sidebar(node),
        WireframeNodeKind.Card => _Card(node),
        WireframeNodeKind.Modal => _Modal(node),
        WireframeNodeKind.Tabs => _Tabs(node),
        WireframeNodeKind.Nav => _Nav(node),
        WireframeNodeKind.Menu => _Menu(node),
        WireframeNodeKind.Breadcrumb => _Breadcrumb(node),
        WireframeNodeKind.Stepper => _Stepper(node),
        WireframeNodeKind.List => _List(node),
        WireframeNodeKind.Table => _Table(node),
        WireframeNodeKind.Item => _Item(node),
        WireframeNodeKind.Label => _Label(node),
        WireframeNodeKind.Button => _Button(node),
        WireframeNodeKind.Input => _Field(node, isSelect: false),
        WireframeNodeKind.Select => _Field(node, isSelect: true),
        WireframeNodeKind.Textarea => _Textarea(node),
        WireframeNodeKind.Search => _Search(node),
        WireframeNodeKind.Checkbox => _Mark(node, isRadio: false),
        WireframeNodeKind.Radio => _Mark(node, isRadio: true),
        WireframeNodeKind.Toggle => _Switch(node),
        WireframeNodeKind.Slider => _Slider(node),
        WireframeNodeKind.Image => _Image(node),
        WireframeNodeKind.Avatar => _Beside(_Round(36, Tint), node),
        WireframeNodeKind.Icon => _Beside(_Round(18, Skeleton, Radius), node),
        WireframeNodeKind.Badge => _Badge(node),
        WireframeNodeKind.Progress => _Progress(node),
        WireframeNodeKind.Pagination => _Pagination(node),
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
                _Text(node.Text ?? "Screen", TitleSize, Ink),
                new Border { Height = 1, Background = Outline },
            },
        };

        // A modal written straight under the screen covers it, rather than taking a row of its own height in the
        // layout — which is what a dialog does to a screen, and the only way the screen under it stays readable.
        var over = node.Children.Where(child => child.Kind == WireframeNodeKind.Modal).ToList();
        var body = new Panel { Children = { _Rows(node.Children.Where(child => !over.Contains(child)).ToList()) } };
        foreach (var modal in over)
        {
            body.Children.Add(Render(modal));
        }

        return new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Pad + 4),
            Child = _Stack(header, body, fillsBody: true),
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
            Child = node.Text is null ? _Skeleton(count: 1) : _Text(node.Text, TextSize, isSelected ? Ink : Muted),
        };
    }

    private static Control _Button(WireframeNode node)
    {
        var isPrimary = node.Has(WireframeModifierName.Primary);
        return new Border
        {
            Background = isPrimary ? Accent : Paper,
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

    private static Control _Mark(WireframeNode node, bool isRadio)
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

    // header and footer are a screen-wide band: whatever they carry sits side by side in it, with the band's own text
    // as the thing on the left — a product name in a header, a copyright line in a footer.
    private static Control _Band(WireframeNode node, Thickness border, double titleSize)
    {
        var contents = _Columns(node.Children);
        return new Border
        {
            Background = Tint,
            BorderBrush = Outline,
            BorderThickness = border,
            Padding = new Thickness(Pad, 10),
            Child = node.Text is null ? contents : _Lead(_Text(node.Text, titleSize, Ink), contents),
        };
    }

    private static Control _Sidebar(WireframeNode node) => new Border
    {
        Background = Tint,
        BorderBrush = Outline,
        BorderThickness = new Thickness(0, 0, 1, 0),
        Padding = new Thickness(Pad),
        MinWidth = 140,
        Child = node.Text is null
            ? _Rows(node.Children)
            : _Stack(_Text(node.Text, CaptionSize, Muted), _Rows(node.Children), fillsBody: true),
    };

    // A card is the tile a screen is built out of — a group with its caption inside the frame instead of above it,
    // which is the whole visual difference between the two on paper.
    private static Control _Card(WireframeNode node) => new Border
    {
        Background = Paper,
        BorderBrush = Outline,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Radius + 2),
        Padding = new Thickness(Pad),
        Child = node.Text is null
            ? _Rows(node.Children)
            : _Stack(_Text(node.Text, TextSize, Ink), _Rows(node.Children), fillsBody: true),
    };

    // A modal is drawn where it is written, over a scrim, because that is what it does to the screen: the rest is
    // still there and no longer reachable.
    private static Control _Modal(WireframeNode node)
    {
        var body = _Rows(node.Children);
        var panel = new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius + 2),
            Padding = new Thickness(Pad + 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = node.Text is null
                ? body
                : _Stack(_Titled(node.Text), body, fillsBody: false),
        };

        return new Border { Background = Scrim, Padding = new Thickness(Pad * 2), Child = panel };
    }

    // A dropdown, drawn open: its text is the trigger above the panel, its items are what the panel holds. A menu
    // nobody can see is not worth putting on a wireframe.
    private static Control _Menu(WireframeNode node)
    {
        var panel = new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _Rows(node.Children, spacing: 2),
        };

        if (node.Text is null)
        {
            return panel;
        }

        var trigger = new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Padding = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Gap,
                Children = { _Text(node.Text, TextSize, Ink), _Text("▾", TextSize, Muted) },
            },
        };

        return _Stack(trigger, panel, fillsBody: false, spacing: 2);
    }

    private static Control _Breadcrumb(WireframeNode node)
    {
        var trail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var child in node.Children)
        {
            if (trail.Children.Count > 0)
            {
                trail.Children.Add(_Text("›", CaptionSize, Muted));
            }

            trail.Children.Add(Render(child));
        }

        return trail;
    }

    // The steps of a wizard, numbered, with everything up to and including the `selected` one drawn as done — a
    // stepper that does not say where you are is a row of circles.
    private static Control _Stepper(WireframeNode node)
    {
        var current = node.Children.FindIndex(child => child.Has(WireframeModifierName.Selected));
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap, VerticalAlignment = VerticalAlignment.Center };
        for (var index = 0; index < node.Children.Count; index++)
        {
            if (index > 0)
            {
                strip.Children.Add(new Border { Width = 24, Height = 1, Background = Outline, VerticalAlignment = VerticalAlignment.Center });
            }

            var done = index <= (current < 0 ? 0 : current);
            var number = _Text($"{index + 1}", CaptionSize, done ? Paper : Muted);
            number.HorizontalAlignment = HorizontalAlignment.Center;
            var circle = _Round(22, done ? Solid : Paper);
            circle.Child = number;
            strip.Children.Add(circle);
            strip.Children.Add(Render(node.Children[index]));
        }

        return strip;
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

    // A label with words shows them; one without shows how much room they take. That is the wireframe convention and
    // the reason a screen can be sketched before anybody has written its copy.
    private static Control _Label(WireframeNode node) =>
        node.Text is null ? _Skeleton(count: 2) : _Text(node.Text, TextSize, Ink);

    // A multi-line field: the same box as an input, tall enough to read as one, with placeholder lines inside when
    // there is nothing in it yet.
    private static Control _Textarea(WireframeNode node)
    {
        var value = node.ValueOf(WireframeModifierName.Value);
        var box = new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            MinHeight = 72,
            Padding = new Thickness(10, 8),
            Child = value is null ? _Skeleton(count: 3) : _Text(value, TextSize, Ink),
        };

        return node.Text is null ? box : _Stack(_Text(node.Text, CaptionSize, Muted), box, fillsBody: false, spacing: 4);
    }

    // Search stands apart from input on purpose: its text is what the box says when it is empty, because that is
    // where a search field carries its wording.
    private static Control _Search(WireframeNode node)
    {
        var value = node.ValueOf(WireframeModifierName.Value);
        return new Border
        {
            Background = Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            MinHeight = ControlHeight,
            Padding = new Thickness(10, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Gap,
                Children = { _Text("⌕", TitleSize, Muted), _Text(value ?? node.Text, TextSize, value is null ? Muted : Ink) },
            },
        };
    }

    private static Control _Switch(WireframeNode node)
    {
        var isOn = node.Has(WireframeModifierName.Checked);
        var knob = _Round(14, Paper);
        knob.Margin = new Thickness(2);
        knob.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        var track = new Border
        {
            Width = 36,
            Height = 20,
            Background = isOn ? Solid : Tint,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Child = knob,
        };

        return _Beside(track, node);
    }

    private static Control _Slider(WireframeNode node)
    {
        var filled = _Percent(node, fallback: 50);
        var track = new Grid { ColumnSpacing = 0, VerticalAlignment = VerticalAlignment.Center, MinHeight = 20 };
        track.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(filled, GridUnitType.Star)));
        track.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        track.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(100 - filled, GridUnitType.Star)));

        var done = new Border { Height = 4, Background = Solid, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        var rest = new Border { Height = 4, Background = Skeleton, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        var knob = _Round(14, Paper);
        Grid.SetColumn(knob, 1);
        Grid.SetColumn(rest, 2);
        track.Children.Add(done);
        track.Children.Add(knob);
        track.Children.Add(rest);

        return node.Text is null ? track : _Stack(_Text(node.Text, CaptionSize, Muted), track, fillsBody: false, spacing: 4);
    }

    // The one place besides a primary button where the accent is allowed: a badge is a count that has to be seen.
    private static Control _Badge(WireframeNode node)
    {
        var isPrimary = node.Has(WireframeModifierName.Primary);
        return new Border
        {
            Background = isPrimary ? Accent : Tint,
            BorderBrush = isPrimary ? Accent : Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 1),
            MinWidth = 22,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _Text(node.ValueOf(WireframeModifierName.Value) ?? node.Text, CaptionSize, isPrimary ? Paper : Ink),
        };
    }

    private static Control _Progress(WireframeNode node)
    {
        var filled = _Percent(node, fallback: 40);
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(filled, GridUnitType.Star)));
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(100 - filled, GridUnitType.Star)));

        var done = new Border { Height = LineHeight, Background = Solid, CornerRadius = new CornerRadius(4) };
        var rest = new Border { Height = LineHeight, Background = Skeleton, CornerRadius = new CornerRadius(4) };
        Grid.SetColumn(rest, 1);
        bar.Children.Add(done);
        bar.Children.Add(rest);

        return node.Text is null ? bar : _Stack(_Text(node.Text, CaptionSize, Muted), bar, fillsBody: false, spacing: 4);
    }

    // Three page boxes around the current one, with the arrows on either side — enough to say "there is more of
    // this" without pretending to know how many pages the real thing has.
    private static Control _Pagination(WireframeNode node)
    {
        var current = Math.Max(1, node.WeightOf(WireframeModifierName.Value) ?? 1);
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        strip.Children.Add(_Page("‹", isCurrent: false));
        for (var page = Math.Max(1, current - 1); page <= Math.Max(1, current - 1) + 2; page++)
        {
            strip.Children.Add(_Page($"{page}", page == current));
        }

        strip.Children.Add(_Text("…", TextSize, Muted));
        strip.Children.Add(_Page("›", isCurrent: false));
        return strip;
    }

    private static Border _Page(string label, bool isCurrent)
    {
        var text = _Text(label, CaptionSize, isCurrent ? Paper : Muted);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            MinWidth = 24,
            Background = isCurrent ? Solid : Paper,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Padding = new Thickness(6, 3),
            Child = text,
        };
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
        var stack = new StackPanel { Spacing = LineGap };
        for (var index = 0; index < count; index++)
        {
            stack.Children.Add(new Border
            {
                Height = LineHeight,
                // A line still has to be seen where nothing stretches it — a breadcrumb entry, a stepper's label.
                MinWidth = 60,
                Background = Skeleton,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // The last line stops short, the way a paragraph does: even lines would read as a stack of bars.
                Margin = new Thickness(0, 0, index == count - 1 ? 60 : 0, 0),
            });
        }

        return stack;
    }

    // A title with the rule under it — a modal's heading, drawn the same way a screen's is.
    private static Control _Titled(string text) => new StackPanel
    {
        Spacing = Gap,
        Children = { _Text(text, TitleSize, Ink), new Border { Height = 1, Background = Outline } },
    };

    // Something that leads a line — a header's title, a footer's caption — with whatever follows taking the rest.
    private static Grid _Lead(Control lead, Control rest)
    {
        var grid = new Grid { ColumnSpacing = Pad };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        Grid.SetColumn(rest, 1);
        grid.Children.Add(lead);
        grid.Children.Add(rest);
        return grid;
    }

    // A mark with the component's own text beside it: an avatar's name, a toggle's wording, an icon's label.
    private static Control _Beside(Control mark, WireframeNode node) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = Gap,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
        Children = { mark, _Text(node.Text, TextSize, Ink) },
    };

    private static Border _Round(double size, IBrush fill, double? radius = null) => new()
    {
        Width = size,
        Height = size,
        Background = fill,
        BorderBrush = Outline,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius ?? (size / 2)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // `value:` as a percentage for the components that draw one, clamped so a typo cannot draw outside its own track.
    private static double _Percent(WireframeNode node, double fallback) =>
        double.TryParse(node.ValueOf(WireframeModifierName.Value), CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 100)
            : fallback;

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
