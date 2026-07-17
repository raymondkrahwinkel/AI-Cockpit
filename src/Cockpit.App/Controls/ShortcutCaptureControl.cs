using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Material.Icons;

namespace Cockpit.App.Controls;

/// <summary>What a <see cref="ShortcutCaptureControl"/> records.</summary>
public enum ShortcutCaptureMode
{
    /// <summary>A full chord, stored as an Avalonia gesture string ("Ctrl+Shift+P"). The default.</summary>
    Chord,

    /// <summary>
    /// A single key, stored as its bare Avalonia <see cref="Key"/> name ("F9"). For the push-to-talk key, which
    /// is a key held rather than a chord and is read back with <c>Enum.TryParse&lt;Key&gt;</c>
    /// (<see cref="Services.PushToTalkKeyGate"/>) — a gesture string would not round-trip through that.
    /// </summary>
    SingleKey,
}

/// <summary>
/// A click-to-record keyboard field (#: shortcuts). Instead of typing "Ctrl+N", the operator clicks it and
/// presses the keys; what they press becomes the bound <see cref="Gesture"/>. <see cref="ShortcutCaptureMode.Chord"/>
/// records a full chord as an Avalonia gesture string ("Ctrl+Shift+P"); <see cref="ShortcutCaptureMode.SingleKey"/>
/// records one bare key as its <see cref="Key"/> name ("F9"), for the push-to-talk key. Escape cancels the
/// capture, and — in chord mode — the ✕ clears the binding. Two-way bindable so a row view model updates live.
/// </summary>
public sealed class ShortcutCaptureControl : UserControl
{
    public static readonly StyledProperty<string> GestureProperty =
        AvaloniaProperty.Register<ShortcutCaptureControl, string>(
            nameof(Gesture), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<ShortcutCaptureMode> ModeProperty =
        AvaloniaProperty.Register<ShortcutCaptureControl, ShortcutCaptureMode>(nameof(Mode));

    public string Gesture
    {
        get => GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public ShortcutCaptureMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private readonly Button _recordButton;
    private readonly Button _clearButton;
    private bool _capturing;

    public ShortcutCaptureControl()
    {
        _recordButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinWidth = 120,
            Focusable = true,
        };
        _recordButton.Click += (_, _) => _BeginCapture();
        _recordButton.AddHandler(KeyDownEvent, _OnKeyDown, RoutingStrategies.Tunnel);
        _recordButton.LostFocus += (_, _) => _EndCapture();

        _clearButton = new Button
        {
            Content = CockpitIcons.Icon(MaterialIconKind.Close, 12),
            Padding = new Thickness(6, 2),
            Classes = { "Subtle" },
            Margin = new Thickness(4, 0, 0, 0),
        };
        ToolTip.SetTip(_clearButton, "Clear this shortcut");
        _clearButton.Click += (_, _) =>
        {
            Gesture = string.Empty;
            _EndCapture();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_recordButton, 0);
        Grid.SetColumn(_clearButton, 1);
        grid.Children.Add(_recordButton);
        grid.Children.Add(_clearButton);
        Content = grid;

        _UpdateLabel();
        _UpdateClearButton();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GestureProperty && !_capturing)
        {
            _UpdateLabel();
        }
        else if (change.Property == ModeProperty)
        {
            _UpdateClearButton();
        }
    }

    // A push-to-talk key is not optional the way a menu shortcut is — clearing it just falls back to the default
    // on save — so the single-key field leaves the ✕ off rather than offering an empty that means "F9".
    private void _UpdateClearButton() => _clearButton.IsVisible = Mode == ShortcutCaptureMode.Chord;

    private void _BeginCapture()
    {
        _capturing = true;
        _recordButton.Content = Mode == ShortcutCaptureMode.SingleKey
            ? "Press a key…  (Esc cancels)"
            : "Press a shortcut…  (Esc cancels)";
        _recordButton.Focus();
    }

    private void _EndCapture()
    {
        if (_capturing)
        {
            _capturing = false;
            _UpdateLabel();
        }
    }

    private void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        // A bare modifier press isn't a complete chord yet — wait for the real key.
        if (_IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _EndCapture();
            e.Handled = true;
            return;
        }

        Gesture = FormatCapturedKey(e.Key, e.KeyModifiers, Mode);
        _capturing = false;
        _UpdateLabel();
        e.Handled = true;
    }

    /// <summary>
    /// How a captured key is stored: single-key mode keeps the bare <see cref="Key"/> name ("F9"), which is what
    /// <see cref="Services.PushToTalkKeyGate"/> parses back with <c>Enum.TryParse&lt;Key&gt;</c>; chord mode keeps
    /// the full Avalonia gesture with its modifiers ("Ctrl+Shift+P"). Pulled out of the key handler so the stored
    /// form is unit-testable without simulating focus and a key press through a window.
    /// </summary>
    internal static string FormatCapturedKey(Key key, KeyModifiers modifiers, ShortcutCaptureMode mode) =>
        mode == ShortcutCaptureMode.SingleKey
            ? key.ToString()
            : new KeyGesture(key, modifiers).ToString();

    private void _UpdateLabel() =>
        _recordButton.Content = string.IsNullOrWhiteSpace(Gesture) ? "Click to set…" : Gesture;

    private static bool _IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;
}
