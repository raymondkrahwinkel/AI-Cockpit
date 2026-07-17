using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Workflows;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// The templates, as a dialog you can search rather than a menu you have to read (#69). A flyout works for three
/// flows; at thirty it is a wall of headings — and the plugins that ship them are only going to get more numerous, so
/// the picker is the thing that has to scale, not the operator's patience.
/// <para>
/// A row says what the flow does and where it came from, because "Ticket → branch → agent" is a name, not an
/// explanation, and picking the wrong one costs you a canvas you then have to read and delete.
/// </para>
/// </summary>
internal sealed class TemplatePickerControl : UserControl
{
    private readonly IReadOnlyList<WorkflowTemplate> _templates;
    private readonly StackPanel _rows;
    private readonly TextBox _search;
    private readonly TextBlock _empty;

    /// <summary>Raised with the template the operator chose; the manager turns it into a flow.</summary>
    public event EventHandler<WorkflowTemplate>? Chosen;

    /// <summary>Raised when the operator would rather open a flow somebody sent them.</summary>
    public event EventHandler? ImportRequested;

    public TemplatePickerControl(IReadOnlyList<WorkflowTemplate> templates)
    {
        _templates = templates;

        _search = new TextBox { PlaceholderText = "Search templates…", Margin = new Thickness(0, 0, 0, 10) };
        _search.TextChanged += (_, _) => _Render();

        _rows = new StackPanel { Spacing = 6 };

        _empty = new TextBlock
        {
            Text = "No templates match that.",
            FontSize = 12,
            Opacity = 0.6,
            IsVisible = false,
        };

        var import = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new MaterialIcon { Kind = MaterialIconKind.Import, Width = 13, Height = 13 },
                    new TextBlock { Text = "Import from file…" },
                },
            },
            Classes = { "Subtle" },
        };
        ToolTip.SetTip(import, "Open a flow somebody exported — it arrives switched off, for you to read before you arm it");
        import.Click += (_, _) => ImportRequested?.Invoke(this, EventArgs.Empty);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(import, Dock.Left);
        footer.Children.Add(import);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = new StackPanel { Children = { _rows, _empty } } });

        Content = root;

        _Render();
    }

    private void _Render()
    {
        var query = _search.Text?.Trim();
        var matching = _templates
            .Where(template => string.IsNullOrEmpty(query) || _Matches(template, query))
            .GroupBy(template => template.Category ?? "Templates")
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _rows.Children.Clear();
        _empty.IsVisible = matching.Count == 0;

        foreach (var group in matching)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.55,
                Margin = new Thickness(2, 8, 0, 2),
            });

            foreach (var template in group)
            {
                _rows.Children.Add(_Row(template));
            }
        }
    }

    // Name, description and the plugin it came from: the operator searching for "review" is as likely to have it in
    // the description as in the name.
    private static bool _Matches(WorkflowTemplate template, string query) =>
        template.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || template.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (template.Category ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase);

    private Control _Row(WorkflowTemplate template)
    {
        var use = new Button { Content = "Use", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };
        use.Click += (_, _) => Chosen?.Invoke(this, template);

        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = template.Name, FontWeight = FontWeight.SemiBold, FontSize = 12.5 },
                new TextBlock
                {
                    Text = template.Description,
                    FontSize = 11,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var row = new DockPanel();
        DockPanel.SetDock(use, Dock.Right);
        row.Children.Add(use);
        row.Children.Add(text);

        var card = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = _Brush("CockpitSecondaryBgBrush"),
            Child = row,
        };

        // The whole card is the target: a row you can read is a row you expect to be able to click.
        card.PointerPressed += (_, _) => Chosen?.Invoke(this, template);

        return card;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
