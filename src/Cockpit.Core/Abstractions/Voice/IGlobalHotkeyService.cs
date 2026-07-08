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

    /// <summary>Registers the hotkey with the OS/desktop and starts listening. Idempotent.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Unregisters the hotkey and stops listening. Idempotent.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
