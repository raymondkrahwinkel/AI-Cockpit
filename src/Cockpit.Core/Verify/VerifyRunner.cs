namespace Cockpit.Core.Verify;

/// <summary>
/// A registered command the verify loop may run for a project (AC-86): it produces a text snapshot of the
/// rendered UI (and optionally a screenshot), which the host feeds back into the session so UI work is not
/// delivered blind (Iron Law #9). The agent can only <em>trigger</em> a registered runner, never choose the
/// command — that is what keeps "verify" from becoming a back door to arbitrary command execution (consent still
/// gates each run). One runner per project is enough for v1, keyed by <see cref="WorkingDirectory"/>: the tool
/// picks the runner whose directory is the session's working directory or an ancestor of it.
/// </summary>
/// <param name="Label">The operator-facing name of the runner, unique in the registry — also the key a save replaces.</param>
/// <param name="WorkingDirectory">The project directory the command runs in, and what the session's working directory is matched against.</param>
/// <param name="Command">The executable to run, verbatim — never a shell string. Passed as the process file name.</param>
/// <param name="Arguments">The command's arguments, passed one by one through <c>ProcessStartInfo.ArgumentList</c> so nothing is re-parsed by a shell.</param>
/// <param name="SnapshotPath">The path the command writes the UI text snapshot to; read back and fed into the session.</param>
/// <param name="ScreenshotPath">The path the command writes an optional PNG screenshot to; attached to the feed additively when the session's provider can see images. Null when the runner produces no screenshot.</param>
/// <param name="CaptureType">How this runner captures — only <see cref="VerifyCaptureType.Avalonia"/> is supported in v1.</param>
public sealed record VerifyRunner(
    string Label,
    string WorkingDirectory,
    string Command,
    IReadOnlyList<string> Arguments,
    string SnapshotPath,
    string? ScreenshotPath = null,
    VerifyCaptureType CaptureType = VerifyCaptureType.Avalonia);
