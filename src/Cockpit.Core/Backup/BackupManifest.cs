namespace Cockpit.Core.Backup;

/// <summary>
/// The note a backup carries about itself (#70), written as <c>backup.json</c> at the root of the archive. Restoring
/// is destructive, and an archive that cannot say what it is has to be trusted on its file name — which is how you end
/// up with someone else's cockpit on your machine.
/// </summary>
/// <param name="Schema">The layout of the archive. A restore refuses a schema it does not know rather than half-applying it.</param>
/// <param name="AppVersion">The cockpit that made it, for the human reading the list of backups.</param>
/// <param name="CreatedUtc">When.</param>
/// <param name="IncludesCredentials">Whether the settings in it still carry their keys and tokens.</param>
/// <param name="RemovedSecrets">The fields that were emptied when it was made — what the operator must type in again after restoring, named rather than left to be discovered one broken plugin at a time.</param>
/// <param name="ProfileConfigDirectories">The profile config directories archived alongside the cockpit's own, by label. Empty when the operator did not include them.</param>
public sealed record BackupManifest(
    int Schema,
    string AppVersion,
    DateTimeOffset CreatedUtc,
    bool IncludesCredentials,
    IReadOnlyList<string> RemovedSecrets,
    IReadOnlyDictionary<string, string> ProfileConfigDirectories)
{
    /// <summary>The archive layout this build writes and reads.</summary>
    public const int CurrentSchema = 1;

    /// <summary>The manifest's own name inside the archive.</summary>
    public const string FileName = "backup.json";

    /// <summary>
    /// Whether this build can restore the archive. A newer schema is refused rather than guessed at: the whole point
    /// of a restore is that afterwards the cockpit is exactly what it was, and a best-effort restore of a layout we do
    /// not know is a cockpit that is *nearly* what it was, which is worse than a refusal you can act on.
    /// </summary>
    public bool CanRestore => Schema == CurrentSchema;
}
