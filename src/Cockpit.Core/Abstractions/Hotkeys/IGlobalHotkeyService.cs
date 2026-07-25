namespace Cockpit.Core.Abstractions.Hotkeys;

/// <summary>
/// Registers the cockpit's desktop-wide hotkeys — keys that fire while the window has no focus. One
/// implementation per OS (the XDG <c>GlobalShortcuts</c> portal on Wayland, a low-level keyboard hook on
/// Windows and X11, nothing on macOS), selected in <c>Cockpit.Infrastructure.DependencyInjection</c> the same
/// way <c>IPtyHostFactory</c> is.
/// </summary>
/// <remarks>
/// It takes a <em>set</em> of bindings rather than the one it used to (#34 shipped only push-to-talk). A
/// second key — the screenshot capture, AC-220 — could otherwise only be had by installing a second keyboard
/// hook and opening a second portal session, which on Wayland also means the operator finding the cockpit
/// listed twice in their desktop's shortcut settings.
/// <para>
/// Threading: <see cref="Pressed"/>/<see cref="Released"/> fire on whatever thread the backend's own event
/// loop uses (the D-Bus main loop on Linux, the keyboard-hook callback thread on Windows) — never the UI
/// thread. Callers must marshal to the UI thread themselves before touching view models or windows; the
/// service does not do this for them.
/// </para>
/// </remarks>
public interface IGlobalHotkeyService
{
    /// <summary>A registered hotkey went down; the argument is its <see cref="GlobalHotkeyBinding.Id"/>.</summary>
    event EventHandler<string>? Pressed;

    /// <summary>A registered hotkey came back up; the argument is its <see cref="GlobalHotkeyBinding.Id"/>. Push-to-talk's hold is the span between the two; a press-only feature ignores this.</summary>
    event EventHandler<string>? Released;

    /// <summary>
    /// How the given hotkey is actually triggered, in words to show the operator — or null when nothing is
    /// armed for it and there is nothing to say.
    /// </summary>
    /// <remarks>
    /// It is reported rather than assumed because on one of the three platforms the cockpit does not decide it.
    /// A Windows hook is armed with the key from the settings and that is that. The XDG portal takes the
    /// configured key as a <em>preferred_trigger</em> — a hint the spec does not oblige a compositor to honour —
    /// and the binding then belongs to the desktop's own shortcut settings, where the operator may change it
    /// without the cockpit hearing of it except through <see cref="TriggerDescriptionsChanged"/>. macOS has no
    /// implementation at all, and null says so.
    /// <para>
    /// The settings field was a text box that looked like it decided all three. It did not, and on Linux it was
    /// not even read.
    /// </para>
    /// </remarks>
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
