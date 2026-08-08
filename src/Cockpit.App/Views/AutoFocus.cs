using Avalonia;
using Avalonia.Controls;

namespace Cockpit.App.Views;

// AC-636: keyboard focus in Avalonia is application-wide — one `KeyboardDevice` for every window — so a control
// focusing itself takes the keyboard out of whichever window the operator is actually in, the assistant's chat
// pop-out included. What was measured, and which routes reach it, is in the ticket.
internal static class AutoFocus
{
    // True when the keyboard currently lives in another window, so focusing something here would take it away.
    // Asked through the public FocusManager: it reports the same application-wide focused element.
    internal static bool WouldTakeTheKeyboardFromAnotherWindow(Visual host) =>
        TopLevel.GetTopLevel(host) is { } here
        && here.FocusManager?.GetFocusedElement() is Visual focused
        && TopLevel.GetTopLevel(focused) is { } elsewhere
        && !ReferenceEquals(elsewhere, here);
}
