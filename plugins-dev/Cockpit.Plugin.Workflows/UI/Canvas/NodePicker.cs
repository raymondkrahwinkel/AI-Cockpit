using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Workflows.Canvas;

// The steps you can add (#69), standing open beside the canvas: every category with its steps under it, each with
// a line saying what it does. Nothing is hidden behind a click — "what can this thing even do" is a question you
// have while looking at the canvas, not one you go and ask.
//
// A step is added by dragging it onto the canvas, where it lands, or by clicking it, in which case it goes where
// there is room. Dragging is what people reach for, and where you drop it *is* where you meant it to go.
internal sealed class NodePicker : Border
{
    // The drag payload: the id of the type being dragged onto the canvas. In-process, because it never leaves the app.
    public static readonly DataFormat<string> DragFormat = DataFormat.CreateInProcessFormat<string>("cockpit/workflow-node-type");

    private const string HintLoose = "Drag one onto the canvas, or click to drop it in.";
    private const string HintAimed = "The next step continues from the + you clicked.";

    private readonly TextBox _search;
    private readonly StackPanel _results;
    private readonly TextBlock _hint;

    private (string NodeId, int Output)? _from;

    public NodePicker()
    {
        Width = 290;
        Background = _Brush("CockpitSecondaryBgBrush", "#0c0e12");
        BorderBrush = _Brush("CockpitHairlineBrush", "#2a2f39");
        BorderThickness = new Thickness(1, 0, 0, 0);

        _search = new TextBox { PlaceholderText = "Search steps…", Margin = new Thickness(12, 8, 12, 8) };
        _search.TextChanged += (_, _) => _Render(_search.Text);
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                AimAtNothing();
                e.Handled = true;
            }
        };

        _results = new StackPanel { Margin = new Thickness(6, 0, 6, 12) };

        // Says what a click will do right now: drop a step somewhere, or continue the way out whose + you clicked.
        _hint = new TextBlock
        {
            Text = HintLoose,
            FontSize = 11,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 8),
        };

        var title = new TextBlock
        {
            Text = "Steps",
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(12, 12, 12, 4),
        };

        Child = new DockPanel
        {
            Children =
            {
                _Docked(title, Dock.Top),
                _Docked(_search, Dock.Top),
                _Docked(_hint, Dock.Top),
                new ScrollViewer { Content = _results },
            },
        };

        // The list is here from the first frame. An earlier version only built it when you searched or clicked a +,
        // so the panel opened empty — which said, wrongly, that there was nothing to add.
        _Render(null);
    }

    // The chosen type, and the way out it should be wired to (null when the step is simply being added).
    public event EventHandler<NodePicked>? Picked;

    // Points the picker at a step's unconnected way out: what you choose next is added *and wired* there.
    public void AimAt(string fromNodeId, int output)
    {
        _from = (fromNodeId, output);
        _hint.Text = HintAimed;
        _search.Focus();
    }

    public void AimAtNothing()
    {
        _from = null;
        _hint.Text = HintLoose;
    }

    private void _Render(string? term)
    {
        _results.Children.Clear();

        var matches = NodeCatalog.Search(term);
        if (matches.Count == 0)
        {
            _results.Children.Add(new TextBlock
            {
                Text = "Nothing matches that.",
                Opacity = 0.6,
                FontSize = 11,
                Margin = new Thickness(8, 8, 0, 0),
            });
            return;
        }

        var searching = !string.IsNullOrWhiteSpace(term);

        foreach (var group in matches.GroupBy(type => type.Heading))
        {
            var steps = new StackPanel();

            foreach (var type in group)
            {
                steps.Children.Add(_Row(type));
            }

            // A search opens everything: what you are looking for is worth more than what you folded away, and a hit
            // hidden inside a collapsed heading is a search that looks broken.
            var open = searching || !_collapsed.Contains(group.Key);
            steps.IsVisible = open;

            var heading = new Button
            {
                Classes = { "Subtle" },
                Padding = new Thickness(8, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 2),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new MaterialIcon
                        {
                            Kind = open ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight,
                            Width = 10,
                            Height = 10,
                            Opacity = 0.45,
                        },
                        new TextBlock
                        {
                            Text = group.Key,
                            FontSize = 10,
                            FontWeight = FontWeight.SemiBold,
                            Opacity = 0.45,
                        },
                    },
                },
            };

            var key = group.Key;
            heading.Click += (_, _) =>
            {
                if (!_collapsed.Add(key))
                {
                    _collapsed.Remove(key);
                }

                _Render(term);
            };

            _results.Children.Add(heading);
            _results.Children.Add(steps);
        }
    }

    // Which headings the operator folded away. Kept for as long as the editor is open, not saved: it is a way of
    // looking at the list right now, not a setting about how they want it forever.
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private Control _Row(NodeTypeDescriptor type)
    {
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = _Radius("CockpitControlRadius", 9),
            Padding = new Thickness(8, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    _Icon(type),
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = type.Name, FontSize = 12 },
                            new TextBlock
                            {
                                Text = type.Description,
                                FontSize = 10,
                                Opacity = 0.55,
                                TextWrapping = TextWrapping.Wrap,
                                MaxWidth = 205,
                            },
                        },
                    },
                },
            },
        };

        ToolTip.SetTip(row, "Drag onto the canvas, or click to add it");

        // The drag carries the type id; the canvas turns the drop point into the step's place. A press that never
        // became a drag comes back as None — that is a click, and it drops the step where there is room.
        row.PointerPressed += async (_, e) =>
        {
            using var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DragFormat, type.Id));

            var effect = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
            if (effect != DragDropEffects.None)
            {
                return;
            }

            // The press never became a drag: that is a click, and it drops the step where there is room.
            Picked?.Invoke(this, new NodePicked(type, _from?.NodeId, _from?.Output ?? 0));
            AimAtNothing();
        };

        return row;
    }

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    // The vector icon when the type has one; the plain glyph otherwise — a plugin's step may still be on the string.
    private static Control _Icon(NodeTypeDescriptor type) => type.IconKind is { } kind
        ? new MaterialIcon { Kind = kind, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center }
        : new TextBlock { Text = type.Icon, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };

    // The host's geometry token, so a plugin's box rounds like the app's other boxes.
    private static CornerRadius _Radius(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);

    // The host's theme brush, resolved at call time. The fallback hex is only reached with no
    // `Application` (designer, headless test) and is held equal to its token by the repository's theme
    // guard.
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}

// What the picker produced: the chosen type, and the way out it should be wired to (when a + was clicked first).
internal sealed record NodePicked(NodeTypeDescriptor Type, string? FromNodeId, int FromOutput);
