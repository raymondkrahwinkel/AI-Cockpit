using Avalonia;
using Avalonia.Controls;

namespace Cockpit.App.Views;

// The one question every "focus this by itself" path has to ask first: is the keyboard even in this window?
// AC-636, measured rather than assumed. Keyboard focus in Avalonia is application-wide, not per window: a
// window's `FocusManager` reads and writes the single process-wide `KeyboardDevice.Instance`
// (Avalonia 12.0.5, `FocusManager.FocusCore` → `keyboardDevice.SetFocusedElement`), and every raw key
// event is routed to whatever that one device holds — `KeyboardDevice.ProcessRawEvent` takes
// `FocusedElement` before it looks at which window the key actually arrived on. So a control calling
// `Focus()` in the main window takes the keyboard out of the assistant's chat pop-out, mid-sentence,
// without either window having been activated or deactivated.
//
// That is what the assistant closing a session did: `CockpitViewModel.CloseSessionAsync` moves
// `SelectedSession` to the next pane (expected, and left alone), `CockpitView` follows every selection
// change by focusing that pane's composer, and the operator typing in the pop-out lost the caret to it. The guard
// therefore sits on the window-activation side only, as asked, and nowhere near the selection logic.
//
// A click or a keystroke that lands in this window is not affected: activating a window restores its own focused
// element first, so by the time a handler runs the keyboard is already here. Nor is startup, where nothing holds
// focus yet and a fresh pane still gets the caret.
internal static class AutoFocus
{
    // True when the keyboard currently lives in another window, so focusing something here would take it away.
    // Asked through the public FocusManager rather than KeyboardDevice.Instance: it is the same application-wide
    // focused element, reached by API this app is allowed to use.
    internal static bool WouldTakeTheKeyboardFromAnotherWindow(Visual host) =>
        TopLevel.GetTopLevel(host) is { } here
        && here.FocusManager?.GetFocusedElement() is Visual focused
        && TopLevel.GetTopLevel(focused) is { } elsewhere
        && !ReferenceEquals(elsewhere, here);
}
