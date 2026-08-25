namespace Cockpit.Core.Backup;

// What a backup of the cockpit is made of (#70). The whole setup lives in one directory, but models
// (gigabytes, re-downloadable) are excluded, and settings carry secrets, so credentials are a
// deliberate opt-in (`BackupOptions.IncludeCredentials`) rather than a default.
public static class BackupContents
{
    // Where a backup or restore does its unpacking and half-finished work. Named here rather than where it is
    // built, because the one thing that must never be forgotten about it is that it is excluded below.
    public const string StagingFolder = "staging";

    // Directories under the cockpit folder that never go into a backup, and why.
    public static IReadOnlyList<string> Excluded { get; } =
    [
        // Gigabytes of Whisper/SupertonicTTS weights, downloadable again.
        "models",

        // Yesterday's log lines restore nothing. They are the app talking to itself.
        "logs",

        // The archive being written lives here (AC-45), so a backup that walked it would reach the file
        // it is holding open and try to put it inside itself — a sharing violation on every run (AC-689).
        StagingFolder,
    ];

    // Whether a path inside the cockpit directory belongs in a backup. `relativePath` uses either separator.
    public static bool Includes(string relativePath)
    {
        var head = relativePath
            .Replace('\\', '/')
            .TrimStart('/')
            .Split('/', 2)[0];

        return !Excluded.Contains(head, StringComparer.OrdinalIgnoreCase);
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
