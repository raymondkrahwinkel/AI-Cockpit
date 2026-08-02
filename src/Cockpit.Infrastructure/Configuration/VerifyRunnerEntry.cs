using Cockpit.Core.Verify;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `VerifyRunner` under the `verifyRunners` section of `cockpit.json`. A
// plain DTO kept apart from the domain record so the persisted shape can evolve on its own, mirroring how
// `WorktreeRegistryEntry` shadows the worktree record.
internal sealed class VerifyRunnerEntry
{
    public string Label { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    public string SnapshotPath { get; set; } = string.Empty;

    public string? ScreenshotPath { get; set; }

    public VerifyCaptureType CaptureType { get; set; } = VerifyCaptureType.Avalonia;

    public static VerifyRunnerEntry FromDomain(VerifyRunner runner) => new()
    {
        Label = runner.Label,
        WorkingDirectory = runner.WorkingDirectory,
        Command = runner.Command,
        Arguments = [.. runner.Arguments],
        SnapshotPath = runner.SnapshotPath,
        ScreenshotPath = runner.ScreenshotPath,
        CaptureType = runner.CaptureType,
    };

    public VerifyRunner ToDomain() => new(
        Label,
        WorkingDirectory,
        Command,
        [.. Arguments],
        SnapshotPath,
        ScreenshotPath,
        CaptureType);
}
