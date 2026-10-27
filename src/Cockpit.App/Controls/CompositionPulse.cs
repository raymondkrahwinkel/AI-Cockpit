using Avalonia;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace Cockpit.App.Controls;

// AC-1829: an opacity pulse that runs on the render thread, so it keeps moving while the UI thread is busy building
// the main window. A style animation would freeze there, and a frozen splash reads as a frozen Cockpit.
public static class CompositionPulse
{
    public static readonly AttachedProperty<bool> IsOnProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsOn", typeof(CompositionPulse));

    static CompositionPulse() => IsOnProperty.Changed.AddClassHandler<Control>((control, _) => _Apply(control));

    public static bool GetIsOn(Control control) => control.GetValue(IsOnProperty);

    public static void SetIsOn(Control control, bool value) => control.SetValue(IsOnProperty, value);

    private static void _Apply(Control control)
    {
        control.AttachedToVisualTree -= _OnAttached;
        control.AttachedToVisualTree += _OnAttached;
        _Run(control);
    }

    private static void _OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => _Run((Control)sender!);

    private static void _Run(Control control)
    {
        if (ElementComposition.GetElementVisual(control) is not { } visual)
        {
            return;
        }

        if (!GetIsOn(control))
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
            return;
        }

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Target = "Opacity";
        animation.InsertKeyFrame(0f, 0.3f);
        animation.InsertKeyFrame(0.5f, 1f);
        animation.InsertKeyFrame(1f, 0.3f);
        animation.Duration = TimeSpan.FromMilliseconds(1200);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", animation);
    }
}
