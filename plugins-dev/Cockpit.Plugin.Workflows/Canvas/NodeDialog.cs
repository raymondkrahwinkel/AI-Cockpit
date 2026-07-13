using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// One step, opened (#69): what flows <em>in</em> on the left, what the step is set to do in the middle, what it
/// produced on the right. The three panes exist because a workflow tool's real question is never "what is this
/// step's name" — it is "what have I got to work with here", and a narrow strip of text boxes cannot answer that.
/// <para>
/// The left pane is the answer to that question and also the way to use it: clicking a field writes its reference
/// into the parameter you were last editing, so the syntax is something you can read rather than something you must
/// remember. Fields of the step before are plain (<c>{output}</c>); fields of any earlier step carry that step's
/// name (<c>{Run a command.output}</c>).
/// </para>
/// <para>
/// Both side panes show what <em>actually</em> flowed in the last run. Before a first run they say so plainly rather
/// than listing what a step might hypothetically produce — a guess in a help text is worse than no help text.
/// </para>
/// </summary>
internal sealed class NodeDialog : Border
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly StackPanel _fields;
    private readonly StackPanel _incoming;
    private readonly StackPanel _outgoing;
    private readonly TextBlock _title;
    private readonly TextBlock _description;
    private readonly Border _card;

    private WorkflowNode? _node;
    private TextBox? _lastEdited;

    public NodeDialog()
    {
        // A scrim, so the canvas behind is visibly out of play while a step is open.
        Background = new SolidColorBrush(Color.Parse("#B0000000"));
        IsVisible = false;

        _title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 15 };
        _description = new TextBlock { FontSize = 11.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

        _fields = new StackPanel { Spacing = 12 };
        _incoming = new StackPanel { Spacing = 6 };
        _outgoing = new StackPanel { Spacing = 6 };

        var close = new Button { Content = "✕", Classes = { "Subtle" } };
        ToolTip.SetTip(close, "Back to the canvas");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var header = new DockPanel { Margin = new Thickness(16, 14, 10, 12) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _description } });

        var panes = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,1.3*,1*"),
            Margin = new Thickness(0, 0, 0, 0),
        };

        var input = _Pane("Comes in", "Click a field to use it in a setting.", _incoming);
        var settings = _Pane("Does this", null, _fields);
        var output = _Pane("Produces", "What it handed on in the last run.", _outgoing);

        Grid.SetColumn(input, 0);
        Grid.SetColumn(settings, 1);
        Grid.SetColumn(output, 2);
        panes.Children.Add(input);
        panes.Children.Add(settings);
        panes.Children.Add(output);

        _card = new Border
        {
            MaxWidth = 980,
            MaxHeight = 560,
            Margin = new Thickness(40),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = _Brush("CockpitSecondaryBgBrush") ?? new SolidColorBrush(Color.Parse("#1E1E24")),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new DockPanel { Children = { _Docked(header, Dock.Top), panes } },
        };

        Child = _card;

        // Clicking the scrim closes; clicking the card must not. Without this the dialog shuts under your own hands
        // the moment you reach for a text box.
        PointerPressed += (_, e) =>
        {
            if (!_card.Bounds.Contains(e.GetPosition(this)))
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Raised when a field changed — the flow is saved as it is edited, like everything else here.</summary>
    public event EventHandler? Changed;

    public event EventHandler? CloseRequested;

    /// <summary>The step currently open, or null.</summary>
    public WorkflowNode? Node => _node;

    /// <summary>
    /// Opens <paramref name="node"/>. <paramref name="incoming"/> is what the last run handed to it,
    /// <paramref name="produced"/> what it handed on, and <paramref name="earlier"/> what every step before it
    /// produced — the data it can reach by name.
    /// </summary>
    public void Show(
        WorkflowNode node,
        IReadOnlyList<JsonObject> incoming,
        IReadOnlyList<JsonObject> produced,
        IReadOnlyList<(string Name, IReadOnlyList<string> Fields)> earlier)
    {
        _node = node;
        _lastEdited = null;
        IsVisible = true;

        _title.Text = node.Name;
        _description.Text = node.Type?.Description ?? $"'{node.TypeId}' is not a step this cockpit knows — a plugin may be missing.";

        _FillSettings(node);
        _FillIncoming(incoming, earlier);
        _FillOutgoing(produced);
    }

    public void Hide()
    {
        IsVisible = false;
        _node = null;
        _lastEdited = null;
    }

    private void _FillSettings(WorkflowNode node)
    {
        _fields.Children.Clear();

        // The name is always editable: three "Notify" steps in one flow are otherwise indistinguishable — and the
        // name is what other steps refer to it by, so it is not decoration.
        _fields.Children.Add(_Field("Name", node.Name, value =>
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                node.Name = trimmed;
                _title.Text = trimmed;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }, referable: false));

        var parameters = node.Type?.Parameters ?? [];

        if (parameters.Count == 0)
        {
            _fields.Children.Add(new TextBlock
            {
                Text = "This step has nothing else to configure.",
                FontSize = 11.5,
                Opacity = 0.5,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (var parameter in parameters)
        {
            node.Parameters.TryGetValue(parameter, out var current);
            _fields.Children.Add(_Field(parameter, current, value =>
            {
                node.Parameters[parameter] = value ?? string.Empty;
                Changed?.Invoke(this, EventArgs.Empty);
            }, referable: true));
        }

        // A step that is switched off stays on the canvas, drawn dimmed, and a run passes it by. Deleting is not the
        // only way to say "not now".
        var disabled = new CheckBox { Content = "Skip this step", IsChecked = node.IsDisabled, Margin = new Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(disabled, "Leave it on the canvas but pass it by when the flow runs.");
        disabled.IsCheckedChanged += (_, _) =>
        {
            node.IsDisabled = disabled.IsChecked == true;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _fields.Children.Add(disabled);
    }

    private void _FillIncoming(IReadOnlyList<JsonObject> incoming, IReadOnlyList<(string Name, IReadOnlyList<string> Fields)> earlier)
    {
        _incoming.Children.Clear();

        var item = incoming.FirstOrDefault();

        if (item is null && earlier.Count == 0)
        {
            _incoming.Children.Add(_Faint("Nothing has flowed into this step yet. Run the flow once and what it receives appears here."));
            return;
        }

        if (item is not null)
        {
            _incoming.Children.Add(_Label("From the step before"));

            foreach (var (key, value) in item)
            {
                _incoming.Children.Add(_FieldChip($"{{{key}}}", value?.ToString() ?? string.Empty));
            }
        }

        // Any step that already ran can be reached by name, which is the difference between a chain and a flow: the
        // notification at the end can quote the command from the middle without every step in between carrying it.
        var reachable = earlier.Where(step => step.Fields.Count > 0).ToList();
        if (reachable.Count > 0)
        {
            _incoming.Children.Add(_Label("From any earlier step"));

            foreach (var (name, fields) in reachable)
            {
                foreach (var field in fields)
                {
                    _incoming.Children.Add(_FieldChip($"{{{name}.{field}}}", name));
                }
            }
        }
    }

    private void _FillOutgoing(IReadOnlyList<JsonObject> produced)
    {
        _outgoing.Children.Clear();

        if (produced.Count == 0)
        {
            _outgoing.Children.Add(_Faint("Run the flow to see what this step produces."));
            return;
        }

        foreach (var item in produced)
        {
            _outgoing.Children.Add(new Border
            {
                Background = _Brush("CockpitPanelBgBrush"),
                BorderBrush = _Brush("CockpitHairlineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Child = new SelectableTextBlock
                {
                    Text = item.ToJsonString(Pretty),
                    FontFamily = new FontFamily("monospace"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }
    }

    // A field you can click: it writes its reference into the parameter you were last in. Typing {Run a command.output}
    // by hand is exactly the kind of thing you get wrong once and then distrust forever.
    private Control _FieldChip(string reference, string value)
    {
        var chip = new Button
        {
            Classes = { "Subtle" },
            Padding = new Thickness(8, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = reference, FontFamily = new FontFamily("monospace"), FontSize = 11 },
                    new TextBlock
                    {
                        Text = value.ReplaceLineEndings(" ").Trim(),
                        FontSize = 10,
                        Opacity = 0.45,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                    },
                },
            },
        };

        ToolTip.SetTip(chip, _lastEditedHint);
        chip.Click += (_, _) => _Insert(reference);

        return chip;
    }

    private const string _lastEditedHint = "Put this in the setting you were last editing.";

    private void _Insert(string reference)
    {
        if (_lastEdited is not { } box)
        {
            return;
        }

        var caret = Math.Clamp(box.CaretIndex, 0, box.Text?.Length ?? 0);
        box.Text = (box.Text ?? string.Empty).Insert(caret, reference);
        box.CaretIndex = caret + reference.Length;
        box.Focus();
    }

    private Control _Field(string label, string? value, Action<string?> onChanged, bool referable)
    {
        var box = new TextBox { Text = value ?? string.Empty, AcceptsReturn = false };

        // Written as you type: the flow is saved continuously, so a value that only lands on Enter would be a value
        // you can lose by clicking away.
        box.TextChanged += (_, _) => onChanged(box.Text);

        if (referable)
        {
            box.GotFocus += (_, _) => _lastEdited = box;
        }

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

    private static Control _Pane(string title, string? hint, StackPanel body)
    {
        var head = new StackPanel { Margin = new Thickness(16, 0, 16, 8) };
        head.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.5,
        });

        if (hint is not null)
        {
            head.Children.Add(new TextBlock { Text = hint, FontSize = 10.5, Opacity = 0.4, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }

        body.Margin = new Thickness(16, 0, 16, 16);

        return new Border
        {
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(0, 1, 1, 0),
            Child = new DockPanel
            {
                Children =
                {
                    _Docked(head, Dock.Top),
                    new ScrollViewer { Content = body },
                },
            },
        };
    }

    private static TextBlock _Label(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Opacity = 0.5,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static TextBlock _Faint(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Opacity = 0.45,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
