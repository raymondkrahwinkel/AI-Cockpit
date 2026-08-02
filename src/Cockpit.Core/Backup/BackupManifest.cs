namespace Cockpit.Core.Backup;

// The note a backup carries about itself (#70), written as `backup.json` at the root of the archive. Restoring
// is destructive, and an archive that cannot say what it is has to be trusted on its file name — which is how you end
// up with someone else's cockpit on your machine.
//
// `Schema`: The layout of the archive. A restore refuses a schema it does not know rather than half-applying it.
// `AppVersion`: The cockpit that made it, for the human reading the list of backups.
// `CreatedUtc`: When.
// `IncludesCredentials`: Whether the settings in it still carry their keys and tokens.
// `RemovedSecrets`: The fields that were emptied when it was made — what the operator must type in again after restoring, named rather than left to be discovered one broken plugin at a time.
// `ProfileConfigDirectories`: The profile config directories archived alongside the cockpit's own, by label. Empty when the operator did not include them.
// `Plugins`: The plugins in this archive — their id and the version that was installed. Read before a restore, so the operator chooses what comes back rather than discovering it afterwards.
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

    // Whether this build can restore the archive. A newer schema is refused rather than guessed at: the whole point
    // of a restore is that afterwards the cockpit is exactly what it was, and a best-effort restore of a layout we do
    // not know is a cockpit that is *nearly* what it was, which is worse than a refusal you can act on.
    public bool CanRestore => Schema == CurrentSchema;
}
