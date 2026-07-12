namespace Cockpit.Plugins.Abstractions;

/// <summary>Actions a plugin can perform on the cockpit itself: put text on the clipboard, or inject it into the active session's input.</summary>
public interface ICockpitActions
{
    Task SetClipboardTextAsync(string text);

    /// <summary>Injects text into the currently selected session — appended to the input box for an SDK session, written to the pty for a TTY session. No-op when <see cref="HasActiveSession"/> is false.</summary>
    Task InjectIntoActiveSessionAsync(string text);

    bool HasActiveSession { get; }

    /// <summary>
    /// Asks the operator to confirm a destructive action (e.g. deleting a saved item) with the cockpit's own
    /// confirmation dialog. Returns true only when they confirm; Cancel/✕/Esc return false. Default returns
    /// true (proceed) so a plugin built against this SDK still works on an older host without the dialog — only
    /// the app's own host shows the real confirmation.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") => Task.FromResult(true);
}
