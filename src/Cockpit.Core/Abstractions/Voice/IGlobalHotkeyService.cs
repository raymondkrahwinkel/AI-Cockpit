namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Registers a single global push-to-talk hotkey that fires even when the cockpit window has no
/// focus — the desktop-wide hold behind the floating voice overlay (#34). One implementation per OS
/// (the XDG <c>GlobalShortcuts</c> portal on Linux, a low-level keyboard hook on Windows), selected in
/// <c>Cockpit.Infrastructure.DependencyInjection</c> the same way <c>IPtyHostFactory</c> is.
/// </summary>
/// <remarks>
/// Threading: <see cref="HoldStarted"/>/<see cref="HoldEnded"/> fire on whatever thread the backend's
/// own event loop uses (the D-Bus main loop on Linux, the keyboard-hook callback thread on Windows) —
/// never the UI thread. Callers must marshal to the UI thread themselves before touching view models
/// or windows; the service does not do this for them.
/// </remarks>
public interface IGlobalHotkeyService
{
    /// <summary>The hotkey was pressed — a hold has started.</summary>
    event EventHandler? HoldStarted;

    /// <summary>The hotkey was released — the hold has ended.</summary>
    event EventHandler? HoldEnded;

    /// <summary>
    /// How this hold is actually triggered, in words to show the operator — or null when nothing is armed and
    /// there is nothing to say.
    /// </summary>
    /// <remarks>
    /// It is reported rather than assumed because on one of the three platforms the cockpit does not decide it.
    /// A Windows hook is armed with the key from the settings and that is that. The XDG portal takes the
    /// configured key as a <em>preferred_trigger</em> — a hint the spec does not oblige a compositor to honour —
    /// and the binding then belongs to the desktop's own shortcut settings, where the operator may change it
    /// without the cockpit hearing of it except through <see cref="TriggerDescriptionChanged"/>. macOS has no
    /// implementation at all, and null says so.
    /// <para>
    /// The settings field was a text box that looked like it decided all three. It did not, and on Linux it was
    /// not even read.
    /// </para>
    /// </remarks>
    string? TriggerDescription { get; }

    /// <summary>Raised when <see cref="TriggerDescription"/> changes — the operator rebound it in their desktop's settings, or it armed. Fires off the UI thread, like the hold events.</summary>
    event EventHandler? TriggerDescriptionChanged;

    /// <summary>Registers the hotkey with the OS/desktop and starts listening. Idempotent.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Unregisters the hotkey and stops listening. Idempotent.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
