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
    // The archive layout this build writes and reads. AC-1276 raised it to 2: an archive now holds a named
    // part of the cockpit directory instead of nearly all of it, which a schema 1 reader would misread as
    // "those folders were empty".
    public const int CurrentSchema = 2;

    // The manifest's own name inside the archive.
    public const string FileName = "backup.json";

    // Whether this build can restore the archive. A layout we do not know is refused rather than guessed at:
    // a best-effort restore leaves the cockpit only *nearly* what it was, which is worse than a refusal you
    // can act on.
    public bool CanRestore => Schema == CurrentSchema;

    // Why not, in words the operator can do something with — null when the archive can be restored. The two
    // directions need different answers: a newer archive waits for an update, an older one never gets one.
    public string? RestoreRefusal => Schema switch
    {
        _ when CanRestore => null,
        < CurrentSchema =>
            $"This backup uses the old layout {Schema}; this cockpit reads {CurrentSchema}. It cannot be restored here. "
            + "Keep the file — it is a zip, and the settings in it can be read out by hand — and make a fresh backup from this cockpit once it is set up.",
        _ =>
            $"This backup was made by a newer cockpit (layout {Schema}, this one reads {CurrentSchema}). Update first — a partial restore of a layout we do not know is worse than none.",
    };
}
