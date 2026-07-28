using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Exclr8.Terminal;

namespace Cockpit.App.Views;

/// <summary>
/// Whether a configured shortcut may fire for the key press that is happening right now, given where the focus
/// is. While the operator types — in a text field or in the embedded terminal — most bindings stand down so
/// they never hijack a keystroke; this is the one place that decides which ones do not.
/// </summary>
/// <remarks>
/// A binding stays live over the terminal when it is "always active" (the command palette, the one escape
/// hatch that must open from anywhere), when its gesture carries two or more modifiers, or when the action
/// itself was marked as staying active there (the session switch). The two-modifier rule exists because such
/// a chord is a command rather than a lone readline/shell key like Ctrl+R, so intercepting it over a terminal
/// costs the shell nothing it wanted.
/// <para>
/// It is a rule about precedence, not about safety, and that distinction is the trap: a two-modifier gesture
/// is taken ahead of a focused text box as well, so whatever the platform or the operator does with that
/// chord while typing is gone. Choosing such a default is therefore a decision about which editing gesture
/// you are prepared to take — see <c>ShortcutCatalog</c>, which records why zoom sits on neither Ctrl+Shift+Z
/// (the platform's second Redo chord) nor any Ctrl+Alt+letter (AltGr arrives as Ctrl+Alt).
/// </para>
/// </remarks>
public static class ShortcutDispatchGate
{
    /// <summary>
    /// Classifies whatever currently holds the keyboard. <c>TerminalControl</c> is a leaf control today, so it
    /// takes focus itself; the walk up the visual tree is what keeps that true if it ever grows a template with
    /// a focusable child. A text box is checked first so it keeps answering for itself either way.
    /// </summary>
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
