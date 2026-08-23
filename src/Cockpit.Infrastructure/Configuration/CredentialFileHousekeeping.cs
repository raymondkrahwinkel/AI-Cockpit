using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Configuration;

// Puts the cockpit's credential-bearing files in order at startup: restricts ones an earlier version left
// at the umask's permissions, and deletes stale `--mcp-config` files. Runs from `Program.Main` rather than
// a lazily-built container singleton, since a session-less start would otherwise never trigger it.
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

            // AC review #8: when encryption is on, a plaintext .bak/.damaged-* copy is pure at-rest exposure
            // with nothing to offer, so it is simply removed here rather than re-encrypted (needs the key).
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

    // AC-435: restricts audit trails an earlier version created at the umask (world-readable on a stock
    // Fedora) — free text that could name a token, path, or customer. A create-mode fix only covers new
    // files, so this migration pass closes the ones already open. Walks only the known trail files.
    internal static void RestrictAuditTrails(string stateDirectory)
    {
        foreach (var trail in AuditTrailFiles.In(stateDirectory))
        {
            // A symlink is followed by the mode change, which would touch a file elsewhere the cockpit
            // never wrote. It only ever creates these paths as regular files, so anything else is skipped.
            if (new FileInfo(trail) is { Exists: true, LinkTarget: null })
            {
                CockpitConfigPath.RestrictExistingFile(trail);
            }
        }
    }

    // AC-46: creates the diagnostic log owner-only (defense-in-depth, not a known leak). The `logs/`
    // directory is created owner-only first, then truncated so each run starts clean; the previous run is
    // kept as `.previous` first, one generation, so "why did the cockpit disappear?" stays answerable.
    public static void PrepareLogFile(string logPath)
    {
        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(directory))
        {
            CockpitConfigPath.EnsurePrivateDirectory(directory);
        }

        if (File.Exists(logPath))
        {
            try
            {
                // Move rather than copy: the owner-only mode this file was created with travels with it, so the
                // kept copy needs no second round of restriction.
                File.Move(logPath, logPath + PreviousLogSuffix, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        CockpitConfigPath.WriteAllTextPrivate(logPath, string.Empty);
    }

    // Appended to the log path for the copy `PrepareLogFile` keeps of the previous run.
    public const string PreviousLogSuffix = ".previous";

    // When `configFilePath` is an encrypted config, deletes any `.bak`/`.damaged-*`
    // sidecar that still holds a credential in the clear. Reads whether encryption is on straight from the config
    // (the `Security` section is not itself a secret), so it works before anything is unlocked.
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
