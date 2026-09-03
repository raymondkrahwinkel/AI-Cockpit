namespace Cockpit.Core.Backup;

// What a backup of the cockpit is made of (#70). The whole setup lives in one directory, but only a named
// part of it is worth carrying back; settings carry secrets, so credentials are a deliberate opt-in
// (`BackupOptions.IncludeCredentials`) rather than a default.
public static class BackupContents
{
    // Where a backup or restore does its unpacking and half-finished work. Named here rather than where it is
    // built, because the one thing that must never be forgotten about it is that it is not in the list below.
    public const string StagingFolder = "staging";

    // AC-1276: the top-level names a backup carries, and nothing else. Listing what stays out is how
    // `worktrees` and `cli` walked in on the day they were added — gigabytes of re-creatable checkouts,
    // silently, because nobody remembered to exclude a folder that did not exist yet.
    public static IReadOnlyList<string> Included { get; } =
    [
        // The settings themselves — what a restore is for.
        "cockpit.json",

        // Which MCP servers the operator allowed; without it a restored cockpit asks every question again.
        "mcp-permission.json",

        // What the assistant was told to remember, and where it left the conversation (AC-595, AC-596).
        "assistant-memory.md",
        "assistant-state.md",

        // The logos the settings point at: five kilobytes, and nothing else knows where the image came from.
        // The plugins themselves are not here — an archive carries a `BackupPluginIndexEntry` per plugin and a
        // restore fetches the binaries from their store again (AC-1275).
        "project-logos",

        // The three small folders AC-1277 weighed and left out, on what they are and not on what they weigh:
        // `worktree-leases` locks checkouts under `worktrees`, which is itself not in here; `claude-provider` is
        // the provider plugin's generated scratch; `statusline` its snapshots of sessions that are already dead.
    ];

    // Whether a path inside the cockpit directory belongs in a backup. `relativePath` uses either separator.
    public static bool Includes(string relativePath)
    {
        var head = relativePath
            .Replace('\\', '/')
            .TrimStart('/')
            .Split('/', 2)[0];

        return Included.Contains(head, StringComparer.OrdinalIgnoreCase);
    }
}

// What the operator chose to put in this backup. The two flags are off by default. `IncludeCredentials`
// strips API keys/tokens when off; `IncludeProfileConfigs` also archives profiles' own config dirs
// (e.g. `~/.claude`); `Plugins` null means all, or a list to restrict to specific plugins.
public sealed record BackupOptions(
    bool IncludeCredentials = false,
    bool IncludeProfileConfigs = false,
    IReadOnlyList<string>? Plugins = null)
{
    // Whether the archive asks a restore to fetch this plugin back. Its stored settings ride along in
    // `cockpit.json` either way (AC-1277) — they are not the plugin, they are what the operator did with it.
    public bool Includes(string pluginId) =>
        Plugins is null || Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase);
}

// What the operator chose to put *back*. A restore replaces things that took a day to set up, so it
// says what it will touch and touches nothing else. `Settings` restores the cockpit's own settings,
// profiles, shortcuts and permissions; `Plugins` restores only the listed plugin ids.
public sealed record RestoreOptions(bool Settings, IReadOnlyList<string> Plugins)
{
    public bool Includes(string pluginId) => Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase);
}

// How far a restore has got, and with it the only question the operator can still be asked: may this be stopped?
// Reported by `IBackupService.RestoreAsync` so the cancel button offers what is actually still possible (AC-1278).
public enum RestoreStage
{
    // Everything so far happens inside staging. Stopping now removes the staging directory and leaves the
    // cockpit exactly as it was.
    Unpacking,

    // The plugins are being fetched from their stores again, since AC-1276 leaves their binaries out of the
    // archive. The one stage with a number worth showing: plugins are countable, an archive's files are not.
    // Still stoppable: this runs before cockpit.json is rewritten, and it is the stage that can take minutes.
    FetchingPlugins,

    // cockpit.json is being rewritten. A half-written one is the state the staging step exists to prevent, so
    // cancellation is no longer honoured past this point.
    Writing,
}

// A restore's position, reported as a stage rather than a percentage (AC-1281): a percentage over tens of
// thousands of archive entries tells the operator nothing, and "which of the three steps" tells them everything.
// `Total` is 0 for the stages that have nothing to count.
public sealed record RestoreProgress(RestoreStage Stage, int Done = 0, int Total = 0);

// A plugin whose settings came back but whose binaries did not — after the restore has tried to fetch them
// (AC-1279). Returned by `IBackupService.RestoreAsync` rather than only logged: a log line is silence to the
// operator, and this is the difference between a restored plugin and a restored plugin that cannot run.
public sealed record RestoreMissingPlugin(string Id, string Reason);

// How a restore ended. `Stopped` is a normal outcome, not a failure: stopping during the plugin fetch leaves the
// settings untouched and whatever landed standing, so it returns what it knows instead of throwing it away.
public sealed record RestoreReport(bool Stopped, IReadOnlyList<RestoreMissingPlugin> MissingPlugins);
