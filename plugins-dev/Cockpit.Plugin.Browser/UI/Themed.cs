using Avalonia;
using Avalonia.Controls;

namespace Cockpit.Plugin.Browser.UI;

internal static class ThemedExtensions
{
    // A host theme resource, followed live, so a switch between Dark and Light repaints without reopening.
    public static T Themed<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }
}
