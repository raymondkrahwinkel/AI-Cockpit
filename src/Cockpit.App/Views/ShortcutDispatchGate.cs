using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Exclr8.Terminal;

namespace Cockpit.App.Views;

// Whether a shortcut may fire given where focus is: bindings stand down while typing, but stay
// live over the terminal when always-active, 2+ modifiers (a command chord, unlike Ctrl+R), or
// marked active there — same 2+ modifier rule beats a focused text box too, see ShortcutCatalog.
public static class ShortcutDispatchGate
{
    // Classifies whatever holds the keyboard. TerminalControl is a leaf today so it takes focus
    // itself; walking the visual tree keeps this true if it ever grows a focusable child.
    public static ShortcutFocus FocusOf(object? focusedElement)
    {
        if (focusedElement is TextBox)
        {
            return ShortcutFocus.TextBox;
        }

        for (var visual = focusedElement as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TerminalControl)
            {
                return ShortcutFocus.Terminal;
            }
        }

        return ShortcutFocus.Elsewhere;
    }

    public static bool IsBindingLive(ShortcutBinding binding, KeyGesture gesture, ShortcutFocus focus)
    {
        if (binding.AlwaysActive || _HasMultipleModifiers(gesture))
        {
            return true;
        }

        return focus switch
        {
            ShortcutFocus.TextBox => false,
            ShortcutFocus.Terminal => binding.ActiveInTerminal,
            _ => true,
        };
    }

    private static bool _HasMultipleModifiers(KeyGesture gesture) =>
        BitOperations.PopCount((uint)gesture.KeyModifiers) >= 2;
}
