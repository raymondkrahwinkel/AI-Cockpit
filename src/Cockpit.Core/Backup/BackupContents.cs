namespace Cockpit.Core.Backup;

// What a backup of the cockpit is made of (#70). The whole setup lives in one directory, which makes this simple —
// but two things in it must never be swept up thoughtlessly.
//
// *The models are not backed up.* Whisper and SupertonicTTS put gigabytes in `models/`, and they can be downloaded
// again in minutes. A 2 GB archive is not a backup: it is a thing you never make twice.
//
// *The settings carry secrets.* API keys for the OpenAI-compatible providers, a Discord webhook, a YouTrack
// token in the plugin storage — all of them sit in `cockpit.json`. So credentials are a deliberate choice per
// backup (`BackupOptions.IncludeCredentials`), and the default is *without*: an archive you drop
// in a cloud folder should not be a key ring.
public static class BackupContents
{
    // Directories under the cockpit folder that never go into a backup, and why.
    public static IReadOnlyList<string> Excluded { get; } =
    [
        // Gigabytes of Whisper/SupertonicTTS weights, downloadable again. This is the difference between a backup you make
        // weekly and one you make once.
        "models",

        // Yesterday's log lines restore nothing. They are the app talking to itself.
        "logs",
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

// What the operator chose to put in this backup. The two flags are off by default, and both are said out loud in the dialog rather than assumed.
//
// `IncludeCredentials`: Keep the API keys, tokens and webhooks in `cockpit.json`. Off: they are stripped, and the restore says what is missing.
// `IncludeProfileConfigs`: Also archive the profiles' own config directories (`~/.claude` and friends), which hold the logins of the agents themselves — outside the cockpit directory, and never a default.
// `Plugins`: Which plugins go in — their binaries *and* everything they saved. Null means all of them, which is what a backup is for; a list is for the operator who wants one plugin's setup and not the rest.
public sealed record BackupOptions(
    bool IncludeCredentials = false,
    bool IncludeProfileConfigs = false,
    IReadOnlyList<string>? Plugins = null)
{
    // Whether a plugin's folder and stored settings belong in this archive.
    public bool Includes(string pluginId) =>
        Plugins is null || Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase);
}

// What the operator chose to put *back*. A restore replaces things that took a day to set up, so it says what it will touch and touches nothing else.
//
// `Settings`: The cockpit's own half: settings, profiles, shortcuts, permissions. False leaves this cockpit's exactly as they are.
// `Plugins`: Which plugins to restore, by id. Empty restores none — a plugin the archive carries is not one the operator necessarily wants back.
public sealed record RestoreOptions(bool Settings, IReadOnlyList<string> Plugins)
{
    public bool Includes(string pluginId) => Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase);
}
