namespace Cockpit.Core.Verify;

// Registered project verify command (AC-86) feeds its UI text snapshot and optional screenshot to the session, avoiding blind UI work.
// Agents trigger but never choose commands, so verify is not arbitrary execution; consent gates each run and one ancestor-matched runner per project suffices for v1.
// Commands are executable names and arguments use `ProcessStartInfo.ArgumentList`, preventing shell reinterpretation.
public sealed record VerifyRunner(
    string Label,
    string WorkingDirectory,
    string Command,
    IReadOnlyList<string> Arguments,
    string SnapshotPath,
    string? ScreenshotPath = null,
    VerifyCaptureType CaptureType = VerifyCaptureType.Avalonia);
