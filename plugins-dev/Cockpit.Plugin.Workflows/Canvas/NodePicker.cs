using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// The steps you can add (#69), standing open beside the canvas: every category with its steps under it, each with
/// a line saying what it does. Nothing is hidden behind a click — "what can this thing even do" is a question you
/// have while looking at the canvas, not one you go and ask.
/// <para>
/// A step is added by dragging it onto the canvas, where it lands, or by clicking it, in which case it goes where
/// there is room. Dragging is what people reach for, and where you drop it <em>is</em> where you meant it to go.
/// </para>
/// </summary>
internal sealed class NodePicker : Border
{
    /// <summary>The drag payload: the id of the type being dragged onto the canvas. In-process, because it never leaves the app.</summary>
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
        Background = _Brush("CockpitSecondaryBgBrush") ?? new SolidColorBrush(Color.Parse("#1E1E24"));
        BorderBrush = _Brush("CockpitHairlineBrush");
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

    /// <summary>The chosen type, and the way out it should be wired to (null when the step is simply being added).</summary>
    public event EventHandler<NodePicked>? Picked;

    /// <summary>Points the picker at a step's unconnected way out: what you choose next is added <em>and wired</em> there.</summary>
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

        foreach (var group in matches.GroupBy(type => type.Category))
        {
            _results.Children.Add(new TextBlock
            {
                Text = _CategoryName(group.Key),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.45,
                Margin = new Thickness(8, 12, 0, 4),
            });

            foreach (var type in group)
            {
                _results.Children.Add(_Row(type));
            }
        }
    }

    private Control _Row(NodeTypeDescriptor type)
    {
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = type.Icon, FontSize = 18, VerticalAlignment = VerticalAlignment.Center },
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

    private static string _CategoryName(NodeCategory category) => category switch
    {
        NodeCategory.Trigger => "STARTS A FLOW",
        NodeCategory.Sessions => "SESSIONS",
        NodeCategory.Notify => "TELL ME",
        NodeCategory.External => "OUTSIDE THE COCKPIT",
        NodeCategory.Flow => "FLOW",
        _ => category.ToString().ToUpperInvariant(),
    };

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}

/// <summary>What the picker produced: the chosen type, and the way out it should be wired to (when a + was clicked first).</summary>
internal sealed record NodePicked(NodeTypeDescriptor Type, string? FromNodeId, int FromOutput);
