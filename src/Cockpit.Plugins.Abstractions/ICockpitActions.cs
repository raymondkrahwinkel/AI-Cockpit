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

    /// <summary>
    /// Opens a new session on the profile named <paramref name="profileLabel"/> — the cockpit's own act, done for a
    /// plugin: a workflow that starts a session on a ticket, a shortcut that opens the session you always open.
    /// The prompt, if any, is handed to it as its first input. <paramref name="workingDirectory"/> overrides the
    /// profile's, for the flow that has just cut a branch in one repo.
    /// <para>
    /// Returns the name the session got, so the caller can say which one it started. Throws when no profile carries
    /// that label — guessing between profiles would run someone's work on the wrong model, in the wrong directory,
    /// with the wrong permissions.
    /// </para>
    /// <para>
    /// Default throws rather than returning quietly: a plugin that asked for a session and got none, with nothing
    /// said, is the worst of the three outcomes. Only the app's own host can actually open one.
    /// </para>
    /// </summary>
    Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null) =>
        throw new NotSupportedException("This host cannot start sessions.");
}
