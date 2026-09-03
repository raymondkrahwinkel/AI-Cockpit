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
    // Whether a plugin's folder and stored settings belong in this archive.
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
