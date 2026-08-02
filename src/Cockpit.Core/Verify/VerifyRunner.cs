namespace Cockpit.Core.Verify;

// A registered command the verify loop may run for a project (AC-86): it produces a text snapshot of the
// rendered UI (and optionally a screenshot), which the host feeds back into the session so UI work is not
// delivered blind (Iron Law #9). The agent can only *trigger* a registered runner, never choose the
// command — that is what keeps "verify" from becoming a back door to arbitrary command execution (consent still
// gates each run). One runner per project is enough for v1, keyed by `WorkingDirectory`: the tool
// picks the runner whose directory is the session's working directory or an ancestor of it.
//
// `Label`: The operator-facing name of the runner, unique in the registry — also the key a save replaces.
// `WorkingDirectory`: The project directory the command runs in, and what the session's working directory is matched against.
// `Command`: The executable to run, verbatim — never a shell string. Passed as the process file name.
// `Arguments`: The command's arguments, passed one by one through `ProcessStartInfo.ArgumentList` so nothing is re-parsed by a shell.
// `SnapshotPath`: The path the command writes the UI text snapshot to; read back and fed into the session.
// `ScreenshotPath`: The path the command writes an optional PNG screenshot to; attached to the feed additively when the session's provider can see images. Null when the runner produces no screenshot.
// `CaptureType`: How this runner captures — only `VerifyCaptureType.Avalonia` is supported in v1.
public sealed record VerifyRunner(
    string Label,
    string WorkingDirectory,
    string Command,
    IReadOnlyList<string> Arguments,
    string SnapshotPath,
    string? ScreenshotPath = null,
    VerifyCaptureType CaptureType = VerifyCaptureType.Avalonia);
