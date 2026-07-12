using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Cockpit.App.Controls;

/// <summary>
/// A click-to-record keyboard-shortcut field (#: shortcuts). Instead of typing "Ctrl+N", the operator clicks
/// it and presses the chord; the pressed keys become the bound <see cref="Gesture"/> (Avalonia form, e.g.
/// "Ctrl+Shift+P"). Escape cancels the capture, and the ✕ clears the binding. Two-way bindable so a row view
/// model's gesture updates live.
/// </summary>
public sealed class ShortcutCaptureControl : UserControl
{
    public static readonly StyledProperty<string> GestureProperty =
        AvaloniaProperty.Register<ShortcutCaptureControl, string>(
            nameof(Gesture), defaultBindingMode: BindingMode.TwoWay);

    public string Gesture
    {
        get => GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    private readonly Button _recordButton;
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

        var clearButton = new Button
        {
            Content = "✕",
            Padding = new Thickness(6, 2),
            Classes = { "Subtle" },
        };
        ToolTip.SetTip(clearButton, "Clear this shortcut");
        clearButton.Click += (_, _) =>
        {
            Gesture = string.Empty;
            _EndCapture();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_recordButton, 0);
        Grid.SetColumn(clearButton, 1);
        clearButton.Margin = new Thickness(4, 0, 0, 0);
        grid.Children.Add(_recordButton);
        grid.Children.Add(clearButton);
        Content = grid;

        _UpdateLabel();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GestureProperty && !_capturing)
        {
            _UpdateLabel();
        }
    }

    private void _BeginCapture()
    {
        _capturing = true;
        _recordButton.Content = "Press a shortcut…  (Esc cancels)";
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

        Gesture = new KeyGesture(e.Key, e.KeyModifiers).ToString();
        _capturing = false;
        _UpdateLabel();
        e.Handled = true;
    }

    private void _UpdateLabel() =>
        _recordButton.Content = string.IsNullOrWhiteSpace(Gesture) ? "Click to set…" : Gesture;

    private static bool _IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;
}
