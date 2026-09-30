namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// Actions a plugin can perform on the cockpit itself: start a session or delegate work to a profile.
/// </summary>
public interface ICockpitActions
{
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
