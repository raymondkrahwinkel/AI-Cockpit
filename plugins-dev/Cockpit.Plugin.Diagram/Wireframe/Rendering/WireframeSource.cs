using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugin.Diagram.Wireframe.Model;

namespace Cockpit.Plugin.Diagram.Wireframe.Rendering;

// The way back from a drawn control to the line it came from (AC-871) — an own attached property rather than Tag,
// which is a shared slot anything else may claim. This is what makes WF-5's hand-editing free.
internal static class WireframeSource
{
    public static readonly AttachedProperty<WireframeNode?> NodeProperty =
        AvaloniaProperty.RegisterAttached<Control, WireframeNode?>("Node", typeof(WireframeSource));

    public static void SetNode(Control control, WireframeNode? node) => control.SetValue(NodeProperty, node);

    public static WireframeNode? GetNode(Control control) => control.GetValue(NodeProperty);
}
