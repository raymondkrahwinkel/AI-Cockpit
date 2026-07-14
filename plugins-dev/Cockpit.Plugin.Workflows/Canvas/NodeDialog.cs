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
    // The setting a field-chip writes into. A Control, not a TextBox: a field the step can enumerate is a dropdown you
    // can type in, and {ticket} has to land in that one too.
    private Control? _lastEdited;

    public NodeDialog()
    {
        // A scrim, so the canvas behind is visibly out of play while a step is open.
        Background = new SolidColorBrush(Color.Parse("#B0000000"));
        IsVisible = false;

        _title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 15 };
        _description = new TextBlock { FontSize = 11.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

        _fields = new StackPanel { Spacing = 14 };
        _incoming = new StackPanel { Spacing = 8 };
        _outgoing = new StackPanel { Spacing = 8 };

        var close = new Button { Content = "✕", Classes = { "Subtle" } };
        ToolTip.SetTip(close, "Back to the canvas");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var header = new DockPanel { Margin = new Thickness(22, 18, 14, 18) };
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
        IReadOnlyList<(string Name, IReadOnlyList<string> Fields)> earlier,
        IReadOnlyList<WorkflowNode> before)
    {
        _node = node;
        _lastEdited = null;
        IsVisible = true;

        _title.Text = node.Name;
        _description.Text = node.Type?.Description ?? $"'{node.TypeId}' is not a step this cockpit knows — a plugin may be missing.";

        _FillSettings(node);
        _FillIncoming(incoming, earlier, before);
        _FillOutgoing(node, produced);
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
            }, referable: true, suggest: node.Type?.Suggest));
        }

        // A step that is switched off stays on the canvas, drawn dimmed, and a run passes it by. Deleting is not the
        // only way to say "not now".
        var disabled = new CheckBox { Content = "Skip this step", IsChecked = node.IsDisabled, Margin = new Thickness(0, 10, 0, 0) };
        ToolTip.SetTip(disabled, "Leave it on the canvas but pass it by when the flow runs.");
        disabled.IsCheckedChanged += (_, _) =>
        {
            node.IsDisabled = disabled.IsChecked == true;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _fields.Children.Add(disabled);

        // The debug switch. When a flow does the wrong thing, what you need is what a step actually handed on — and
        // the one-line summary in the run log is a summary, not the data.
        // The blunt version of an error path, for when there is nowhere to send the failure. A wire from the step's
        // red "error" pin says more and says it better — and it wins over this, because someone who drew a wire meant
        // the failure to go somewhere.
        if (node.Kind != WorkflowNodeKind.Trigger)
        {
            var carryOn = new CheckBox { Content = "Keep going if this fails", IsChecked = node.ContinueOnError, Margin = new Thickness(0, -4, 0, 0) };
            ToolTip.SetTip(carryOn, "The flow continues down the ordinary wire instead of stopping. For steps whose failure is not the point — a notification nobody received should not stop a deploy that worked.");
            carryOn.IsCheckedChanged += (_, _) =>
            {
                node.ContinueOnError = carryOn.IsChecked == true;
                Changed?.Invoke(this, EventArgs.Empty);
            };
            _fields.Children.Add(carryOn);
        }

        var traced = new CheckBox { Content = "Print what it produces", IsChecked = node.IsTraced, Margin = new Thickness(0, -4, 0, 0) };
        ToolTip.SetTip(traced, "Write everything this step hands on into the run log, in full.");
        traced.IsCheckedChanged += (_, _) =>
        {
            node.IsTraced = traced.IsChecked == true;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _fields.Children.Add(traced);
    }

    private void _FillIncoming(
        IReadOnlyList<JsonObject> incoming,
        IReadOnlyList<(string Name, IReadOnlyList<string> Fields)> earlier,
        IReadOnlyList<WorkflowNode> before)
    {
        _incoming.Children.Clear();

        var item = incoming.FirstOrDefault();

        if (item is not null)
        {
            _incoming.Children.Add(_Label("From the step before"));

            foreach (var (key, value) in item)
            {
                _incoming.Children.Add(_FieldChip($"{{{key}}}", value?.ToString() ?? string.Empty));
            }
        }
        else if (before.Count > 0)
        {
            // No run yet — but the steps wired before this one say what they typically hand on, and knowing the
            // shape of your input before you press Execute is the difference between writing a setting and guessing
            // at one. Labelled an example, so it is never mistaken for what actually happened.
            var samples = before
                .SelectMany(step => (step.Type?.Produces ?? new Dictionary<string, string>()).Select(field => (step, field)))
                .ToList();

            if (samples.Count == 0)
            {
                _incoming.Children.Add(_Faint("Nothing has flowed into this step yet. Run the flow once and what it receives appears here."));
            }
            else
            {
                _incoming.Children.Add(_Label("From the step before — example, until you run it"));

                foreach (var (step, field) in samples)
                {
                    _incoming.Children.Add(_FieldChip($"{{{field.Key}}}", $"e.g. {field.Value}   ({step.Name})"));
                }
            }
        }
        else
        {
            _incoming.Children.Add(_Faint("Nothing flows into this step: it is where a run begins."));
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

    private void _FillOutgoing(WorkflowNode node, IReadOnlyList<JsonObject> produced)
    {
        _outgoing.Children.Clear();

        if (produced.Count == 0)
        {
            var sample = node.Type?.Produces ?? new Dictionary<string, string>();

            if (sample.Count == 0)
            {
                _outgoing.Children.Add(_Faint("Run the flow to see what this step produces."));
                return;
            }

            _outgoing.Children.Add(_Faint("An example of what this step hands on. Run the flow to see the real thing."));

            var example = new JsonObject();
            foreach (var (key, value) in sample)
            {
                example[key] = value;
            }

            _outgoing.Children.Add(_Json(example, faint: true));
            return;
        }

        foreach (var item in produced)
        {
            _outgoing.Children.Add(_Json(item, faint: false));
        }
    }

    private static Control _Json(JsonObject item, bool faint) => new Border
    {
        Background = _Brush("CockpitPanelBgBrush"),
        BorderBrush = _Brush("CockpitHairlineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 10),
        Opacity = faint ? 0.6 : 1,
        Child = new SelectableTextBlock
        {
            Text = item.ToJsonString(Pretty),
            FontFamily = new FontFamily("monospace"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        },
    };

    // A field you can click: it writes its reference into the parameter you were last in. Typing {Run a command.output}
    // by hand is exactly the kind of thing you get wrong once and then distrust forever.
    private Control _FieldChip(string reference, string value)
    {
        var chip = new Button
        {
            Classes = { "Subtle" },
            Padding = new Thickness(10, 7),
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
        switch (_lastEdited)
        {
            case TextBox box:
            {
                // At the caret: a reference usually goes into the middle of a sentence ("Work on {ticket}: {summary}").
                var caret = Math.Clamp(box.CaretIndex, 0, box.Text?.Length ?? 0);
                box.Text = (box.Text ?? string.Empty).Insert(caret, reference);
                box.CaretIndex = caret + reference.Length;
                box.Focus();
                break;
            }

            case AutoCompleteBox picker:
            {
                // A dropdown's value is the whole value — "{state}" is what goes in it, not a word inside a sentence.
                picker.Text = reference;
                picker.Focus();
                break;
            }
        }
    }

    private Control _Field(
        string label,
        string? value,
        Action<string?> onChanged,
        bool referable,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? suggest = null)
    {
        // A field the step can enumerate becomes a dropdown you can still type in: the statuses a board allows, but
        // also {state} from the step before — a field that will not take an expression is a field that fights the
        // flow. An AutoCompleteBox is both; a plain ComboBox would be only the first.
        if (suggest is not null)
        {
            var picker = new AutoCompleteBox
            {
                Text = value ?? string.Empty,
                Padding = new Thickness(10, 7),
                FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
                MinimumPrefixLength = 0,
                IsTextCompletionEnabled = false,
                PlaceholderText = "Choose one, or write an expression",
            };
            picker.TextChanged += (_, _) => onChanged(picker.Text);

            if (referable)
            {
                picker.GotFocus += (_, _) => _lastEdited = picker;
            }

            _ = _SuggestAsync(suggest, label, picker);

            return new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 },
                    picker,
                },
            };
        }

        var box = new TextBox { Text = value ?? string.Empty, AcceptsReturn = false };

        // Written as you type: the flow is saved continuously, so a value that only lands on Enter would be a value
        // you can lose by clicking away.
        box.TextChanged += (_, _) => onChanged(box.Text);

        if (referable)
        {
            box.GotFocus += (_, _) => _lastEdited = box;
        }

        box.Padding = new Thickness(10, 7);

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 },
                box,
            },
        };
    }

    // Asked once, when the step is opened. A failure is silence: a field whose suggestions cannot be fetched (the
    // token is wrong, the server is down) is still a field you can type in, and an error about it would be an error
    // about something the operator did not ask for.
    private static async Task _SuggestAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> suggest,
        string parameter,
        AutoCompleteBox picker)
    {
        try
        {
            picker.ItemsSource = await suggest(parameter, CancellationToken.None);
        }
        catch (Exception)
        {
            // Nothing to offer; the box stays a box.
        }
    }

    private static Control _Pane(string title, string? hint, StackPanel body)
    {
        var head = new StackPanel { Margin = new Thickness(20, 16, 20, 12) };
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

        body.Margin = new Thickness(20, 0, 20, 20);

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
