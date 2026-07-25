namespace Cockpit.Core.Abstractions.Hotkeys;

/// <summary>
/// One desktop-wide hotkey the cockpit asks for: which feature it belongs to, what to call it where the
/// operator can see it, and the key wanted for it.
/// </summary>
/// <param name="Id">
/// Stable identifier for the feature this key drives — <see cref="GlobalHotkeys"/> holds the ones in use. It
/// travels with every press so one subscriber can tell its own key from another's, and on Linux it is also
/// the identity the compositor stores the binding under, so changing it would silently orphan whatever the
/// operator had bound.
/// </param>
/// <param name="Description">What the desktop's own shortcut settings list this as, e.g. "Push to talk (hold)".</param>
/// <param name="KeyName">Avalonia <c>Key</c> enum name, e.g. "F9" — a request, not a guarantee: see <see cref="IGlobalHotkeyService.TriggerDescriptionFor"/>.</param>
public sealed record GlobalHotkeyBinding(string Id, string Description, string KeyName);
