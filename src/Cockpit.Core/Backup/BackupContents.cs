namespace Cockpit.Core.Backup;

/// <summary>
/// What a backup of the cockpit is made of (#70). The whole setup lives in one directory, which makes this simple —
/// but two things in it must never be swept up thoughtlessly.
/// <para>
/// <b>The models are not backed up.</b> Whisper and Piper put gigabytes in <c>models/</c>, and they can be downloaded
/// again in minutes. A 2 GB archive is not a backup: it is a thing you never make twice.
/// </para>
/// <para>
/// <b>The settings carry secrets.</b> API keys for the OpenAI-compatible providers, a Discord webhook, a YouTrack
/// token in the plugin storage — all of them sit in <c>cockpit.json</c>. So credentials are a deliberate choice per
/// backup (<see cref="BackupOptions.IncludeCredentials"/>), and the default is <em>without</em>: an archive you drop
/// in a cloud folder should not be a key ring.
/// </para>
/// </summary>
public static class BackupContents
{
    /// <summary>Directories under the cockpit folder that never go into a backup, and why.</summary>
    public static IReadOnlyList<string> Excluded { get; } =
    [
        // Gigabytes of Whisper/Piper weights, downloadable again. This is the difference between a backup you make
        // weekly and one you make once.
        "models",

        // Yesterday's log lines restore nothing. They are the app talking to itself.
        "logs",
    ];

    /// <summary>Whether a path inside the cockpit directory belongs in a backup. <paramref name="relativePath"/> uses either separator.</summary>
    public static bool Includes(string relativePath)
    {
        var head = relativePath
            .Replace('\\', '/')
            .TrimStart('/')
            .Split('/', 2)[0];

        return !Excluded.Contains(head, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>What the operator chose to put in this backup. Both are off by default, and both are said out loud in the dialog rather than assumed.</summary>
/// <param name="IncludeCredentials">Keep the API keys, tokens and webhooks in <c>cockpit.json</c>. Off: they are stripped, and the restore says what is missing.</param>
/// <param name="IncludeProfileConfigs">Also archive the profiles' own config directories (<c>~/.claude</c> and friends), which hold the logins of the agents themselves — outside the cockpit directory, and never a default.</param>
public sealed record BackupOptions(bool IncludeCredentials = false, bool IncludeProfileConfigs = false);
