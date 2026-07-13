using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// "What happens next?" (#69) — the panel that slides in when you click the <c>+</c> on a step's way out, or ask
/// for a new one. A search box and the types grouped by what you would be looking for, each with a line saying
/// what it does. This is the part of n8n worth taking: you find a step by describing it, not by knowing where it
/// was filed.
/// </summary>
internal sealed class NodePicker : Border
{
    private readonly TextBox _search;
    private readonly StackPanel _results;

    public NodePicker()
    {
        Width = 300;
        Background = _Brush("CockpitSecondaryBgBrush") ?? new SolidColorBrush(Color.Parse("#1E1E24"));
        BorderBrush = _Brush("CockpitHairlineBrush");
        BorderThickness = new Thickness(1, 0, 0, 0);
        IsVisible = false;

        _search = new TextBox { Watermark = "Search steps…", Margin = new Thickness(12, 12, 12, 8) };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };
        _search.TextChanged += (_, _) => _Render(_search.Text);

        _results = new StackPanel { Margin = new Thickness(6, 0, 6, 12) };

        var header = new DockPanel { Margin = new Thickness(12, 12, 8, 0) };
        var close = new Button { Content = "✕", Classes = { "Subtle", "Compact" } };
        close.Click += (_, _) => Hide();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = "What happens next?",
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Child = new DockPanel
        {
            Children =
            {
                _Docked(header, Dock.Top),
                _Docked(_search, Dock.Top),
                new ScrollViewer { Content = _results },
            },
        };
    }

    /// <summary>The chosen type, and the way out it should be wired to (null when the operator just wanted a step somewhere).</summary>
    public event EventHandler<NodePicked>? Picked;

    private (string NodeId, int Output)? _from;

    /// <summary>Opens the picker for a step's unconnected way out: whatever is chosen is added <em>and wired</em>, which is the whole reason the + exists.</summary>
    public void ShowFor(string fromNodeId, int output)
    {
        _from = (fromNodeId, output);
        _Open();
    }

    /// <summary>Opens the picker with nothing to wire to — the "add a step" case.</summary>
    public void ShowLoose()
    {
        _from = null;
        _Open();
    }

    public void Hide() => IsVisible = false;

    private void _Open()
    {
        _search.Text = string.Empty;
        _Render(null);
        IsVisible = true;
        _search.Focus();
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
                Opacity = 0.5,
                Margin = new Thickness(8, 10, 0, 4),
            });

            foreach (var type in group)
            {
                _results.Children.Add(_Row(type));
            }
        }
    }

    private Button _Row(NodeTypeDescriptor type)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6),
            Background = Brushes.Transparent,
            Content = new StackPanel
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
                                Opacity = 0.6,
                                TextWrapping = TextWrapping.Wrap,
                                MaxWidth = 210,
                            },
                        },
                    },
                },
            },
        };

        button.Click += (_, _) =>
        {
            Picked?.Invoke(this, new NodePicked(type, _from?.NodeId, _from?.Output ?? 0));
            Hide();
        };

        return button;
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

/// <summary>What the picker produced: the chosen type, and the way out it should be wired to (when it was opened from a "+").</summary>
internal sealed record NodePicked(NodeTypeDescriptor Type, string? FromNodeId, int FromOutput);
