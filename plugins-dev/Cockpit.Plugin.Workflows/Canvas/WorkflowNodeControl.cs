using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// One step on the canvas (#69). The interaction is n8n's, because it is proven; the look is the cockpit's, because
/// a copy of someone else's app is not a product. So: a wide card, not a square icon tile — the same compact,
/// hairline-bordered row the cockpit uses everywhere else, with the icon on the left, the name you gave it, and the
/// type underneath in small print.
/// <para>
/// A coloured edge says what kind of step it is at a glance — accent for a trigger (where a run begins), a cool
/// stripe for an action, an amber one for a decision. A trigger is also rounded on its leading edge, so the start of
/// a flow has a shape and not just a colour.
/// </para>
/// </summary>
internal sealed class WorkflowNodeControl : Border
{
    public const double CardWidth = 172;
    public const double CardHeight = 60;

    private readonly List<WorkflowPin> _outputs = [];
    private readonly WorkflowPin? _input;
    private readonly Border _card;

    public WorkflowNodeControl(WorkflowNode node)
    {
        Node = node;
        Background = Brushes.Transparent;

        var isTrigger = node.Kind == WorkflowNodeKind.Trigger;

        var edge = new Border
        {
            Width = 4,
            Background = _KindBrush(node.Kind),
            CornerRadius = isTrigger ? new CornerRadius(_CardCorner, 0, 0, _CardCorner) : new CornerRadius(3, 0, 0, 3),
        };

        var icon = node.Type?.IconKind is { } kind
            ? new MaterialIcon
            {
                Kind = kind,
                Width = 17,
                Height = 17,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0),
            }
            : (Control)new TextBlock
            {
                Text = node.Type?.Icon ?? "?",
                FontSize = 17,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0),
            };

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = node.Name,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 110,
                },
                new TextBlock
                {
                    Text = _Subtitle(node),
                    FontSize = 9,
                    Opacity = 0.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 110,
                },
            },
        };

        // A double-click is invisible: nothing on a card says it can be opened. The gear says it, and it is the
        // thing a hand goes to anyway.
        var gear = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.Cog, Width = 11, Height = 11 },
            Classes = { "Subtle", "Compact" },
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 4, 0),
            Opacity = 0.55,
        };
        ToolTip.SetTip(gear, "What this step is set to do");
        gear.Click += (_, _) => Opened?.Invoke(this, EventArgs.Empty);

        _card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            Background = _Brush("CockpitPanelBgBrush", "#1a1d24"),
            BorderBrush = _Hairline,
            BorderThickness = new Thickness(1),
            // A trigger's leading edge is round: a flow visibly starts somewhere rather than merely having a
            // leftmost box.
            CornerRadius = isTrigger
                ? new CornerRadius(CardHeight / 2, _CardCorner, _CardCorner, CardHeight / 2)
                : new CornerRadius(_CardCorner),
            ClipToBounds = true,
            Child = new Panel
            {
                Children =
                {
                    new DockPanel { Children = { _Docked(edge, Dock.Left), _Docked(icon, Dock.Left), text } },
                    gear,
                },
            },
        };

        // The double-click is read from the press itself, not from Avalonia's DoubleTapped: the first click starts
        // a drag and captures the pointer, and a captured pointer never delivers the second tap. That is why
        // double-clicking a step did nothing at all — the settings were there, unreachable.
        _card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount >= 2)
            {
                Opened?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            HeaderPressed?.Invoke(this, e);
        };
        _card.Cursor = new Cursor(StandardCursorType.SizeAll);
        ToolTip.SetTip(_card, node.Type?.Description
            ?? $"This flow uses '{node.TypeId}', which this cockpit does not have — a plugin may be missing.");

        var inputColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (node.HasInput)
        {
            _input = _BuildPin(isInput: true, outputIndex: -1);
            inputColumn.Children.Add(_input);
        }

        // A decision's two ways out are labelled on the pin itself, so which branch is which is readable before you
        // follow the wire anywhere.
        var outputColumn = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        for (var index = 0; index < node.Outputs.Count; index++)
        {
            var pin = _BuildPin(isInput: false, outputIndex: index);
            _outputs.Add(pin);

            var label = node.Outputs.ElementAtOrDefault(index) ?? string.Empty;
            outputColumn.Children.Add(label.Length == 0
                ? pin
                : new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = label, FontSize = 9, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center },
                        pin,
                    },
                });
        }

        // The way out a failure takes. Drawn in the colour of trouble and always last, so a flow's happy path reads
        // straight across and the thing that goes wrong hangs below it. A trigger has none: it cannot fail, it either
        // fires or it does not. And it is off unless the step asks for it — a red pin on every step of every flow is
        // a decision nobody made, on a canvas that should show the ones they did.
        if (node.Kind != WorkflowNodeKind.Trigger && node.HasErrorPath)
        {
            var error = _BuildPin(isInput: false, outputIndex: node.ErrorOutput);
            error.Fill = ErrorBrush;
            _outputs.Add(error);

            outputColumn.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = "error", FontSize = 9, Opacity = 0.5, Foreground = ErrorBrush, VerticalAlignment = VerticalAlignment.Center },
                    error,
                },
            });
        }

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Opacity = node.IsDisabled ? 0.4 : 1,
            Children = { inputColumn, _card, outputColumn },
        };
    }

    public WorkflowNode Node { get; }

    public event EventHandler<PointerPressedEventArgs>? HeaderPressed;

    /// <summary>The step was double-clicked: open what it can be configured with. Double-click is the gesture people already try on a node.</summary>
    public event EventHandler? Opened;

    /// <summary>An output pin was pressed: the canvas starts drawing a wire and captures the pointer, so the drop lands wherever the operator lets go.</summary>
    public event Action<WorkflowNodeControl, WorkflowPin, IPointer>? PinPressed;

    /// <summary>Selection is a ring in the cockpit's accent, not a colour change — the card's own edge already says what kind of step it is.</summary>
    public bool IsSelected
    {
        set
        {
            _card.BorderBrush = value ? _Accent : _Hairline;
            _card.BorderThickness = new Thickness(value ? 2 : 1);
        }
    }

    /// <summary>How the last run treated this step — a ring around the card, so a failed flow shows you where it broke without opening anything.</summary>
    public void ShowRunStatus(string? statusKey)
    {
        if (statusKey is null)
        {
            _card.BorderBrush = _Hairline;
            _card.BorderThickness = new Thickness(1);
            return;
        }

        // The key comes from the run, not from this file, so there is no single fallback to pair it with — an
        // unresolvable status simply leaves the card on its resting edge.
        _card.BorderBrush = Application.Current?.TryFindResource(statusKey, out var status) == true && status is IBrush brush
            ? brush
            : _Hairline;
        _card.BorderThickness = new Thickness(2);
    }

    /// <summary>Lit while a wire is dragged over this step: the target of a drop should be obvious before you let go.</summary>
    public bool IsDropTarget
    {
        set
        {
            if (value)
            {
                _card.BorderBrush = _Accent;
                _card.BorderThickness = new Thickness(2);
            }
        }
    }

    public WorkflowPin OutputPin(int index) => _outputs.FirstOrDefault(pin => pin.OutputIndex == index) ?? _outputs[0];

    /// <summary>
    /// The colour of a failure, on the pin and on the wire that leaves by it. A property rather than a once-computed
    /// static: a static is resolved when the type first loads and keeps that brush for the life of the process,
    /// which is the same freezing the host's own helper exists to avoid.
    /// </summary>
    public static IBrush ErrorBrush => _Brush("CockpitStatusErrorBrush", "#D64545");

    public WorkflowPin InputPin() => _input ?? _outputs[0];

    /// <summary>Lights the pin a dragged wire would attach to — null clears it.</summary>
    public void HighlightInput(bool lit)
    {
        if (_input is not null)
        {
            _input.IsHighlighted = lit;
        }
    }

    public IReadOnlyList<WorkflowPin> OutputPins => _outputs;

    private WorkflowPin _BuildPin(bool isInput, int outputIndex)
    {
        var pin = new WorkflowPin(this, isInput, outputIndex)
        {
            Margin = isInput ? new Thickness(0, 0, -5, 0) : new Thickness(-5, 0, 0, 0),
        };

        pin.PointerPressed += (_, e) =>
        {
            PinPressed?.Invoke(this, pin, e.Pointer);
            e.Handled = true;
        };

        return pin;
    }

    // Cockpit colours, not n8n's: the accent for what starts a run, a cool stripe for work, amber for a fork.
    // Under the name, what this step is actually set to do — the command it runs, the message it sends. Repeating
    // the step's own name there (which is what a fresh step is called) said the same thing twice; a card should tell
    // you what it does without being opened. An unconfigured step falls back to its type, which is the honest answer.
    private static string _Subtitle(WorkflowNode node)
    {
        var configured = (node.Type?.Parameters ?? [])
            .Select(parameter => node.Parameters.GetValueOrDefault(parameter))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return configured ?? node.Type?.Name ?? node.TypeId;
    }

    /// <summary>
    /// The stripe down a card's leading edge, saying what kind of step it is. A categorical palette, not a set of
    /// meanings: the card's <em>border</em> is the channel that carries run status, so painting a kind in a status
    /// colour would leave a decision node looking permanently blocked next to one that actually is.
    /// <para>
    /// A trigger takes the app's accent — a flow's starting point is the one thing on the canvas that should follow
    /// the accent wherever it goes. That is why the other two have to stay clear of it, and why the plain-step
    /// stripe is no longer <c>#5B7FA6</c>: against the old orange it was simply "blue", and against the accent's own
    /// blue (AC-334) it became a slightly-faded copy of the trigger. It is a neutral slate now, with no hue to
    /// confuse — a plain step should read as plain.
    /// </para>
    /// </summary>
    private static IBrush _KindBrush(WorkflowNodeKind kind) => kind switch
    {
        WorkflowNodeKind.Trigger => _Accent,
        WorkflowNodeKind.Decision => new SolidColorBrush(Color.Parse("#C79A4A")),
        _ => new SolidColorBrush(Color.Parse("#7A8290")),
    };

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static IBrush _Hairline => _Brush("CockpitHairlineBrush", "#2a2f39");

    private static IBrush _Accent => _Brush("CockpitAccentBrush", "#2563eb");

    /// <summary>
    /// A card's corner, from the theme's control radius, so a step on the canvas rounds like every other box in the
    /// app. Taken as a single number rather than the <see cref="CornerRadius"/> itself because a trigger's card is
    /// round down one side and needs the corners set one at a time.
    /// </summary>
    private static double _CardCorner =>
        Application.Current?.TryFindResource("CockpitControlRadius", out var value) == true && value is CornerRadius radius
            ? radius.TopLeft
            : 9;

    /// <summary>
    /// The host's theme brush, resolved at call time. The fallback hex is only reached with no
    /// <see cref="Application"/> (designer, headless test) and is held equal to its token by the repository's theme
    /// guard.
    /// </summary>
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}

/// <summary>A connection point: pull a wire out of a way out, and drop it on the step it should run to.</summary>
internal sealed class WorkflowPin : Ellipse
{
    public WorkflowPin(WorkflowNodeControl owner, bool isInput, int outputIndex)
    {
        Owner = owner;
        IsInput = isInput;
        OutputIndex = outputIndex;

        Width = 10;
        Height = 10;
        Fill = Idle;
        Cursor = new Cursor(StandardCursorType.Hand);

        var isError = !isInput && outputIndex == owner.Node.ErrorOutput;
        var label = isInput ? null : owner.Node.Outputs.ElementAtOrDefault(outputIndex);

        ToolTip.SetTip(this, isInput
            ? "Where this step's input comes in"
            : isError
                ? "Where a failure goes. Wire it and this step failing does not stop the flow — it carries on here, with {error} and {step} to say what went wrong."
                : string.IsNullOrEmpty(label)
                    ? "Drag from here onto the next step"
                    : $"The '{label}' branch — drag from here onto the next step");
    }

    public WorkflowNodeControl Owner { get; }

    public bool IsInput { get; }

    /// <summary>Which way out this is (a decision has two); -1 for an input.</summary>
    public int OutputIndex { get; }

    /// <summary>
    /// Lit while a wire is being dragged onto this pin: it grows and takes the accent, so the exact point the wire
    /// will attach to is visible before you let go. The card lighting up says <em>which step</em>; this says
    /// <em>where</em>.
    /// </summary>
    public bool IsHighlighted
    {
        set
        {
            Width = Height = value ? 15 : 10;
            Fill = value ? _Brush("CockpitAccentBrush", "#2563eb") : Idle;
        }
    }

    /// <summary>A pin nobody is dragging onto: the theme's faint text, the quietest ink the app owns.</summary>
    private static IBrush Idle => _Brush("CockpitTextFaintBrush", "#656c78");

    /// <summary>
    /// The host's theme brush, resolved at call time. A copy of the one on <see cref="WorkflowNodeControl"/> because
    /// this is a type of its own; the fallback hex is only reached with no <see cref="Application"/> (designer,
    /// headless test) and is held equal to its token by the repository's theme guard.
    /// </summary>
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));

    /// <summary>Where the wire attaches, in canvas coordinates — asked fresh on every redraw, because the pin moves with its step.</summary>
    public Point AnchorOn(Visual surface) => this.TranslatePoint(new Point(Width / 2, Height / 2), surface) ?? default;
}
