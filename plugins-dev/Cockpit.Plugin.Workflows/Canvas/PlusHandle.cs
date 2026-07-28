using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// The <c>+</c> that hangs off a way out with nothing after it (#69): click it and the picker asks what comes
/// next; drag it onto a step and you have drawn a wire to it. Both, because both are what a hand reaches for.
/// <para>
/// Deliberately <em>not</em> a <see cref="Button"/>. A Button marks the pointer press as handled in its own class
/// handler, before any handler you add — so a drag started on it never begins. That is exactly what went wrong the
/// first time: neither the click nor the drag worked, and the control looked like a button the whole time.
/// </para>
/// </summary>
internal sealed class PlusHandle : Border
{
    private const double Size = 18;

    public PlusHandle(WorkflowPin pin)
    {
        Pin = pin;

        Width = Size;
        Height = Size;
        CornerRadius = new CornerRadius(Size / 2);
        Background = _Brush("CockpitPanelBgBrush", "#1a1d24");
        BorderBrush = _Brush("CockpitHairlineBrush", "#2a2f39");
        BorderThickness = new Thickness(1);
        Cursor = new Cursor(StandardCursorType.Hand);

        Child = new TextBlock
        {
            Text = "+",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8,
        };

        ToolTip.SetTip(this, "Click to add the next step — or drag from here onto a step to connect them");

        PointerEntered += (_, _) => BorderBrush = _Brush("CockpitAccentBrush", "#3b82f6");
        PointerExited += (_, _) => BorderBrush = _Brush("CockpitHairlineBrush", "#2a2f39");
        PointerPressed += (_, e) =>
        {
            Pressed?.Invoke(this, e);
            e.Handled = true;
        };
    }

    public WorkflowPin Pin { get; }

    /// <summary>The handle was pressed — the canvas starts a wire and captures the pointer; a release without a drag is a click.</summary>
    public event EventHandler<PointerPressedEventArgs>? Pressed;

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
