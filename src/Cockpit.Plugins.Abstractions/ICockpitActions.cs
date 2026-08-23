namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// Actions a plugin can perform on the cockpit itself: put text on the clipboard, or inject it into the active session's input.
/// </summary>
public interface ICockpitActions
{
    Task SetClipboardTextAsync(string text);

    /// <summary>
    /// Injects text into the currently selected session — appended to the input box for an SDK session, written to the pty for a TTY session. No-op when <see cref="HasActiveSession"/> is false.
    /// </summary>
    Task InjectIntoActiveSessionAsync(string text);

    bool HasActiveSession { get; }

    /// <summary>
    /// Asks the operator to confirm a destructive action (e.g. deleting a saved item) with the cockpit's own
    /// confirmation dialog. Returns true only when they confirm; Cancel/close/Esc return false. Default returns
    /// true (proceed) so a plugin built against this SDK still works on an older host without the dialog — only
    /// the app's own host shows the real confirmation.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") => Task.FromResult(true);

    /// <summary>
    /// Opens a new session on the profile named <paramref name="profileLabel"/>, with <paramref name="prompt"/> as
    /// its first input. Returns the name the session got; throws when no profile carries that label.
    /// </summary>
    /// <remarks>
    /// <paramref name="workingDirectory"/> overrides the profile's own. Default throws rather than returning
    /// quietly — only the app's own host can actually open a session.
    /// </remarks>
    Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null) =>
        throw new NotSupportedException("This host cannot start sessions.");

    /// <summary>
    /// <see cref="StartSessionAsync(string, string?, string?)"/>, with the session's name said up front (#AC-312).
    /// Left null, the profile and the clock name it instead.
    /// </summary>
    /// <remarks>
    /// A separate overload, not a fourth parameter on the one above, for binary compatibility (#AC-40): an older
    /// host that predates this member has no default to fall back to, so it fails outright rather than silently
    /// using the three-argument behaviour — <c>minHostVersion</c> is what keeps a plugin off such a host.
    /// Implement both overloads or neither.
    /// </remarks>
    Task<string> StartSessionAsync(string profileLabel, string? prompt, string? workingDirectory, string? sessionName) =>
        throw new NotSupportedException("This host cannot start sessions.");

    /// <summary>
    /// Sets the statusline shown under the active (selected) session's name, and optionally renames it (#AC-13).
    /// No-op when there is no active session.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> <paramref name="statusline"/> leaves it, an empty string clears it; a blank
    /// <paramref name="name"/> leaves the title. Default no-op so a plugin built against this SDK still works on
    /// an older host.
    /// </remarks>
    Task SetActiveSessionStatusAsync(string? statusline = null, string? name = null) => Task.CompletedTask;

    /// <summary>
    /// Hands work to another profile as a background task and waits for what it produces (#67, #69). The task
    /// appears in the delegated-tasks view like any other.
    /// </summary>
    /// <remarks>
    /// Throws when the profile refused the work, when it failed, or when <paramref name="timeout"/> passes.
    /// </remarks>
    /// <param name="profileLabel">
    /// The profile to hand it to. It must have opted in as a delegation target.
    /// </param>
    /// <param name="prompt">
    /// The work.
    /// </param>
    /// <param name="workingDirectory">
    /// Where it runs, when the profile allows one to be named.
    /// </param>
    /// <param name="timeout">
    /// How long to wait for an answer. Null waits as long as the host's own default.
    /// </param>
    Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory = null, TimeSpan? timeout = null) =>
        throw new NotSupportedException("This host cannot delegate work.");

    /// <summary>
    /// The same, saying what the task may do (AC-971). Left out, a delegated task runs READ-ONLY — file writes and
    /// shell commands are refused by the host itself.
    /// </summary>
    /// <remarks>
    /// Pass <c>"acceptEdits"</c> for a task meant to change files, or <c>"bypassPermissions"</c> to also let it run
    /// commands; anything above the target profile's own ceiling is put to the operator rather than granted. A
    /// separate overload, not another optional parameter, so an already-compiled plugin keeps binding to the
    /// overload above and gets the safe read-only default.
    /// </remarks>
    Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory, TimeSpan? timeout, string? permission) =>
        DelegateAsync(profileLabel, prompt, workingDirectory, timeout);
}
