using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// What one step is configured with (#69): its name, and the parameters its type declares. Opened by
/// double-clicking a step — the gesture people already try on a node — and shown where the step picker was, so the
/// canvas keeps its space.
/// <para>
/// The fields come from the type (<see cref="NodeTypeDescriptor.Parameters"/>), and the values go straight into the
/// step. They are plain text for now: what a parameter may refer to — the data the previous step produced — is a
/// decision the cockpit has not made yet, and inventing an expression language before the engine exists would be
/// inventing it twice.
/// </para>
/// </summary>
internal sealed class NodeSettingsPanel : Border
{
    private readonly StackPanel _fields;
    private readonly TextBlock _title;
    private readonly TextBlock _description;
    private readonly StackPanel _empty;

    private WorkflowNode? _node;

    public NodeSettingsPanel()
    {
        Width = 290;
        Background = _Brush("CockpitSecondaryBgBrush") ?? new SolidColorBrush(Color.Parse("#1E1E24"));
        BorderBrush = _Brush("CockpitHairlineBrush");
        BorderThickness = new Thickness(1, 0, 0, 0);
        IsVisible = false;

        _title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        _description = new TextBlock { FontSize = 11, Opacity = 0.55, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        _fields = new StackPanel { Spacing = 10, Margin = new Thickness(12, 12, 12, 12) };

        _empty = new StackPanel
        {
            Margin = new Thickness(12, 8, 12, 0),
            Children =
            {
                new TextBlock
                {
                    Text = "This step has nothing to configure.",
                    FontSize = 11,
                    Opacity = 0.55,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var close = new Button { Content = "✕", Classes = { "Subtle", "Compact" } };
        ToolTip.SetTip(close, "Back to the steps");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var header = new DockPanel { Margin = new Thickness(12, 12, 8, 0) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _title, _description },
        });

        Child = new DockPanel
        {
            Children =
            {
                _Docked(header, Dock.Top),
                new ScrollViewer { Content = new StackPanel { Children = { _empty, _fields } } },
            },
        };
    }

    /// <summary>Raised when a field changed — the flow is saved as it is edited, like everything else here.</summary>
    public event EventHandler? Changed;

    public event EventHandler? CloseRequested;

    public void Show(WorkflowNode node)
    {
        _node = node;
        IsVisible = true;

        _title.Text = node.Name;
        _description.Text = node.Type?.Description ?? $"'{node.TypeId}' is not a step this cockpit knows — a plugin may be missing.";

        _fields.Children.Clear();

        // The name is always editable: three "Notify" steps in one flow are otherwise indistinguishable.
        _fields.Children.Add(_Field("Name", node.Name, value =>
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                node.Name = trimmed;
                _title.Text = trimmed;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }));

        var parameters = node.Type?.Parameters ?? [];
        _empty.IsVisible = parameters.Count == 0;

        foreach (var parameter in parameters)
        {
            node.Parameters.TryGetValue(parameter, out var current);
            _fields.Children.Add(_Field(parameter, current, value =>
            {
                node.Parameters[parameter] = value ?? string.Empty;
                Changed?.Invoke(this, EventArgs.Empty);
            }));
        }

        // A step that is switched off stays on the canvas, drawn dimmed, and a run skips it. Deleting is not the
        // only way to say "not now".
        var disabled = new CheckBox { Content = "Skip this step", IsChecked = node.IsDisabled, Margin = new Thickness(0, 6, 0, 0) };
        ToolTip.SetTip(disabled, "Leave it on the canvas but pass it by when the flow runs.");
        disabled.IsCheckedChanged += (_, _) =>
        {
            node.IsDisabled = disabled.IsChecked == true;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _fields.Children.Add(disabled);
    }

    public void Hide()
    {
        IsVisible = false;
        _node = null;
    }

    /// <summary>The step currently open, or null — so the editor can tell whether a change concerns it.</summary>
    public WorkflowNode? Node => _node;

    private static Control _Field(string label, string? value, Action<string?> onChanged)
    {
        var box = new TextBox { Text = value ?? string.Empty };

        // Written as you type: the flow is saved continuously, so a value that only lands on Enter would be a value
        // you can lose by clicking away.
        box.TextChanged += (_, _) => onChanged(box.Text);

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 },
                box,
            },
        };
    }

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
