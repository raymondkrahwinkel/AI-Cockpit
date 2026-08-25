namespace Cockpit.Core.Backup;

// The note a backup carries about itself (#70), written as `backup.json` at the root of the archive.
// `Schema` lets a restore refuse a layout it doesn't know; `RemovedSecrets` names the fields the
// operator must type in again after restoring.
public sealed record BackupManifest(
    int Schema,
    string AppVersion,
    DateTimeOffset CreatedUtc,
    bool IncludesCredentials,
    IReadOnlyList<string> RemovedSecrets,
    IReadOnlyDictionary<string, string> ProfileConfigDirectories,
    IReadOnlyDictionary<string, string> Plugins)
{
    // The archive layout this build writes and reads.
    public const int CurrentSchema = 1;

    // The manifest's own name inside the archive.
    public const string FileName = "backup.json";

    // Whether this build can restore the archive. A newer schema is refused rather than guessed at:
    // a best-effort restore of an unknown layout leaves the cockpit only *nearly* what it was, which
    // is worse than a refusal you can act on.
    public bool CanRestore => Schema == CurrentSchema;
}
