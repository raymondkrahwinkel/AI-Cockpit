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

    /// <summary>
    /// Sets the statusline shown under the active (selected) session's name — what it is working on — and optionally
    /// renames it (#AC-13): the workflow half of the feature, so a flow that started a session on a ticket can label
    /// it with the ticket number, and clear it when the work moves on. The active session is the one a preceding
    /// start-session step just opened and selected. A <see langword="null"/> <paramref name="statusline"/> leaves it,
    /// an empty string clears it; a blank <paramref name="name"/> leaves the title. No-op when there is no active
    /// session. Default no-op so a plugin built against this SDK still works on an older host.
    /// </summary>
    Task SetActiveSessionStatusAsync(string? statusline = null, string? name = null) => Task.CompletedTask;

    /// <summary>
    /// Hands work to another profile as a background task and waits for what it produces (#67, #69) — the cockpit's
    /// own delegation, done for a plugin. The task appears in the delegated-tasks view like any other, because an
    /// agent working invisibly is exactly what this project does not do.
    /// <para>
    /// Returns what the profile answered. Throws when it refused the work, when it failed, or when
    /// <paramref name="timeout"/> passes — a caller that got no answer must not be handed an empty string and left to
    /// treat it as one.
    /// </para>
    /// </summary>
    /// <param name="profileLabel">The profile to hand it to. It must have opted in as a delegation target.</param>
    /// <param name="prompt">The work.</param>
    /// <param name="workingDirectory">Where it runs, when the profile allows one to be named.</param>
    /// <param name="timeout">How long to wait for an answer. Null waits as long as the host's own default.</param>
    Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory = null, TimeSpan? timeout = null) =>
        throw new NotSupportedException("This host cannot delegate work.");
}
