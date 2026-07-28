using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Puts the cockpit's credential-bearing files in order at startup: restricts the ones an earlier version wrote
/// at the umask's permissions, and deletes the <c>--mcp-config</c> files an earlier version left behind.
/// <para>
/// This runs from <c>Program.Main</c> rather than from the constructor of whatever happens to touch these files,
/// because both jobs must happen on every start whether or not anything triggers them. A container singleton is
/// built lazily — an operator who opens no TTY session would never construct the launcher, and the stale token
/// in the temp directory would simply stay there.
/// </para>
/// </summary>
public static class CredentialFileHousekeeping
{
    public static void Run()
    {
        try
        {
            CockpitConfigPath.EnsurePrivateDirectory(CockpitConfigPath.Root);
            CockpitConfigPath.RestrictExistingFile(CockpitConfigPath.Default);
            CockpitConfigPath.RestrictExistingFile(Path.Combine(CockpitConfigPath.Root, "mcp-permission.json"));
            RestrictAuditTrails(CockpitConfigPath.Root);

            // The sidecars of a save that was killed halfway. Each carries a full copy of the config — profiles,
            // provider keys, MCP bearer headers — so a leftover is the whole file lying around under another name.
            CockpitConfigPath.SweepStaleSidecars(CockpitConfigPath.Default);

            // When encryption is on, the live config is ciphertext and is the source of truth — so a plaintext .bak
            // or .damaged-* copy is pure at-rest exposure with nothing to offer. This runs before unlock (reading
            // "is encryption on" needs no key), so an abandoned unlock cannot leave the plaintext lying around all
            // session. Re-encrypting one needs the key and stays a nicety in UnlockAsync; here it is simply removed
            // (review #8).
            RemoveEncryptedConfigPlaintextSidecars(CockpitConfigPath.Default);

            TtyMcpConfigFile.SweepStale();

            // The statusline snapshots of killed sessions are swept by the provider plugin that now owns the
            // statusline (Fase 4), at its own startup — the host no longer holds any provider's statusline files.
        }
        catch (Exception)
        {
            // Housekeeping never keeps the operator out of their cockpit. The write paths set the permissions
            // themselves, so a failure here costs the migration of an old file, not the protection of a new one.
        }
    }

    /// <summary>
    /// Restricts the audit trails a version before AC-435 created at the umask — world-readable on a stock Fedora.
    /// The trails are not credential files, but they are the record of what was approved, what a sub-agent was asked
    /// to do and what one agent told another: free text nothing stops from naming a token, a path or a customer.
    /// <para>
    /// The write path was fixed to create them owner-only, and a create mode only applies to a file being created —
    /// so every machine that ran an earlier build still has its trails lying open, and only a pass like this one
    /// closes them. It walks the known trails (<see cref="AuditTrailFiles"/>) rather than everything in the state
    /// root: a file the cockpit did not write is not ours to change the mode of.
    /// </para>
    /// </summary>
    internal static void RestrictAuditTrails(string stateDirectory)
    {
        foreach (var trail in AuditTrailFiles.In(stateDirectory))
        {
            // A symlink is followed by the mode change, so a link that happens to carry a trail's name would hand
            // this pass a file somewhere else entirely — one the cockpit never wrote and whose permissions are the
            // operator's business. The cockpit only ever creates these paths as regular files, so anything else
            // under the name is not the trail this is here to close.
            if (new FileInfo(trail) is { Exists: true, LinkTarget: null })
            {
                CockpitConfigPath.RestrictExistingFile(trail);
            }
        }
    }

    /// <summary>
    /// Creates (truncating) the diagnostic log owner-only — the private-write treatment every file under the state
    /// root gets (AC-46). No secret content flows to the log today, but it sits beside files that are all owner-only,
    /// and a stock umask would otherwise leave it world-readable; defense-in-depth, not a fix for a known leak. The
    /// <c>logs/</c> directory is created owner-only first, and the file is truncated so each run starts clean —
    /// matching <c>FileLoggerProvider</c>'s own contract, which then only ever appends to this already-restricted file.
    /// Lives here because this is the one public seam that reaches the file-permission logic (<c>CockpitConfigPath</c>).
    /// </summary>
    public static void PrepareLogFile(string logPath)
    {
        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(directory))
        {
            CockpitConfigPath.EnsurePrivateDirectory(directory);
        }

        CockpitConfigPath.WriteAllTextPrivate(logPath, string.Empty);
    }

    /// <summary>
    /// When <paramref name="configFilePath"/> is an encrypted config, deletes any <c>.bak</c>/<c>.damaged-*</c>
    /// sidecar that still holds a credential in the clear. Reads whether encryption is on straight from the config
    /// (the <c>Security</c> section is not itself a secret), so it works before anything is unlocked.
    /// </summary>
    internal static void RemoveEncryptedConfigPlaintextSidecars(string configFilePath)
    {
        if (!IsEncryptionEnabled(configFilePath))
        {
            return;
        }

        DeleteIfHoldsPlaintextSecret(configFilePath + ".bak");

        var directory = Path.GetDirectoryName(configFilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var damaged in Directory.EnumerateFiles(directory, $"{Path.GetFileName(configFilePath)}.damaged-*"))
        {
            DeleteIfHoldsPlaintextSecret(damaged);
        }
    }

    private static bool IsEncryptionEnabled(string configFilePath)
    {
        if (ReadJson(configFilePath) is not JsonObject document)
        {
            return false;
        }

        return document["Security"] is JsonObject security
            && security["Enabled"] is JsonValue enabled
            && enabled.TryGetValue<bool>(out var isEnabled)
            && isEnabled;
    }

    private static void DeleteIfHoldsPlaintextSecret(string path)
    {
        if (ReadJson(path) is not { } document)
        {
            return;
        }

        // The name rule alone (SecretFields.ByName): plugin-declared field names are not known this early — before
        // any plugin has loaded — but the built-in credential names are, and those are what a stale plaintext copy
        // most exposes. A value already ciphertext is left uncounted, so a fully-encrypted sidecar is not deleted.
        var plaintext = SecretJsonWalker.Transform(
            document,
            SecretFields.ByName,
            (_, value) => SecretProtector.IsProtected(value) ? null : value);

        if (plaintext.Count == 0)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A locked leftover is untidy, not dangerous — the next start tries again.
        }
    }

    private static JsonNode? ReadJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }
}
