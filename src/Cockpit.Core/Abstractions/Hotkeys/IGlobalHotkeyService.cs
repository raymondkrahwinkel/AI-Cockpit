namespace Cockpit.Core.Abstractions.Hotkeys;

/// <summary>
/// Registers the cockpit's desktop-wide hotkeys — keys that fire while the window has no focus. One implementation
/// per OS (the XDG <c>GlobalShortcuts</c> portal on Wayland, a low-level keyboard hook on Windows and X11, nothing on macOS), selected in <c>Cockpit.Infrastructure.DependencyInjection</c> like <c>IPtyHostFactory</c>. Takes a <em>set</em> of bindings rather than the one it used to (#34 shipped only push-to-talk) — a second key (AC-220 screenshot capture) would otherwise need a second keyboard hook/portal session, doubling the cockpit's Wayland shortcut entry. Threading: <see cref="Pressed"/>/<see cref="Released"/> fire on the backend's own event-loop thread, never the UI thread — callers must marshal themselves.
/// </summary>
public interface IGlobalHotkeyService
{
    /// <summary>A registered hotkey went down; the argument is its <see cref="GlobalHotkeyBinding.Id"/>.</summary>
    event EventHandler<string>? Pressed;

    /// <summary>A registered hotkey came back up; the argument is its <see cref="GlobalHotkeyBinding.Id"/>. Push-to-talk's hold is the span between the two; a press-only feature ignores this.</summary>
    event EventHandler<string>? Released;

    /// <summary>
    /// How the given hotkey is actually triggered, in words to show the operator — or null when nothing is armed
    /// for it. Reported rather than assumed: a Windows hook is simply armed with the settings key, but the XDG
    /// portal takes it only as a <em>preferred_trigger</em> hint, and the binding then belongs to the desktop's own shortcut settings, changeable without the cockpit hearing except via <see cref="TriggerDescriptionsChanged"/>; macOS has no implementation, hence null.
    /// </summary>
    string? TriggerDescriptionFor(string hotkeyId);

    /// <summary>Raised when any trigger description changes — the operator rebound one in their desktop's settings, or a binding armed. Fires off the UI thread, like the key events.</summary>
    event EventHandler? TriggerDescriptionsChanged;

    /// <summary>
    /// Registers exactly the given hotkeys with the OS/desktop and starts listening. Replaces whatever was
    /// registered before, so this is also how a rebound or switched-off key takes effect; an empty list
    /// registers nothing.
    /// </summary>
    Task StartAsync(IReadOnlyList<GlobalHotkeyBinding> bindings, CancellationToken cancellationToken = default);

    /// <summary>Unregisters everything and stops listening. Idempotent.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
