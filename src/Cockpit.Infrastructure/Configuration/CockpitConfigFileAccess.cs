using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

// Shared read-modify-write access to `cockpit.json`: every section loads the full file, mutates its
// own part, and writes the whole document back, so no store clobbers a sibling's section. Also where
// credentials are encrypted/decrypted, covering every section including a plugin's own storage.
internal sealed class CockpitConfigFileAccess(string configFilePath, ISecretKeyHolder? keyHolder = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // The last version that read cleanly, written by every save — and what a damaged config is recovered from.
    private const string BackupSuffix = ".bak";

    // How long a read waits out a writer holding the file. A swap is milliseconds; reaching this means something other than contention.
    private static readonly TimeSpan ReadContentionWindow = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReadContentionInterval = TimeSpan.FromMilliseconds(20);

    private readonly ISecretKeyHolder _keyHolder = keyHolder ?? SecretKeyHolder.Shared;

    public string ConfigFilePath => configFilePath;

    public async Task<CockpitConfigFile?> ReadAsync(CancellationToken cancellationToken)
    {
        if (await TryReadAsync(configFilePath, cancellationToken).ConfigureAwait(false) is { } configFile)
        {
            return configFile;
        }

        // The file exists but does not parse. Treating it as absent would let a caller write an empty
        // document back over everything, so the last known-good `.bak` is tried first instead.
        if (File.Exists(configFilePath)
            && await TryReadAsync(configFilePath + BackupSuffix, cancellationToken).ConfigureAwait(false) is { } recovered)
        {
            var damaged = $"{configFilePath}.damaged-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
            File.Move(configFilePath, damaged, overwrite: true);
            File.Copy(configFilePath + BackupSuffix, configFilePath, overwrite: true);
            CockpitConfigPath.RestrictExistingFile(configFilePath);

            return recovered;
        }

        if (File.Exists(configFilePath))
        {
            // Neither the file nor its backup reads. Refusing beats starting empty and overwriting what is there —
            // the operator can look at the file, and whatever is in it is still in it.
            throw new InvalidOperationException(
                $"The cockpit configuration at {configFilePath} is unreadable, and so is its backup. It has been left "
                + "untouched rather than started over.");
        }

        return null;
    }

    private async Task<CockpitConfigFile?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await ReadWhenNotBeingReplacedAsync(path, cancellationToken).ConfigureAwait(false);
            var document = JsonNode.Parse(json);
            if (document is null)
            {
                return null;
            }

            if (_keyHolder.Protector is { } protector)
            {
                SecretJsonWalker.Transform(document, _keyHolder.Fields, (path, value) =>
                    SecretProtector.IsProtected(value) ? protector.Unprotect(path, value) : null);
            }

            return document.Deserialize<CockpitConfigFile>(SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Reads the file, waiting out a writer mid-swap (`File.Replace` holds the destination and a reader
    // that lands there gets a sharing violation) rather than failing outright — the 2026-07-15 incident.
    // Internal so `SecretProtectionService` (review #9) reads through the same retry.
    internal static async Task<string> ReadWhenNotBeingReplacedAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ReadContentionWindow;
        while (true)
        {
            try
            {
                return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            // FileNotFoundException included on purpose (AC-1047): the swap frees the name for an instant between
            // moving the old file to `.bak` and renaming the new one in, so a reader that lands there finds
            // nothing — both callers checked the file exists before getting here.
            catch (IOException exception) when (exception is not DirectoryNotFoundException
                                                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(ReadContentionInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Loads the file, mutates one section, and writes the whole document back, serialised against every
    // other writer on this machine — without the gate, two concurrent writers each silently restore the
    // other's section to what it had been (how a plugin's freshly pinned hash once disappeared).
    public async Task UpdateAsync(Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(configFilePath, cancellationToken).ConfigureAwait(false);

        var configFile = await ReadAsync(cancellationToken).ConfigureAwait(false) ?? new CockpitConfigFile();
        mutate(configFile);

        var document = JsonSerializer.SerializeToNode(configFile, SerializerOptions)
            ?? throw new InvalidOperationException("The cockpit configuration serialized to nothing.");

        var protector = _keyHolder.Protector;
        if (protector is not null)
        {
            SecretJsonWalker.Transform(document, _keyHolder.Fields, (path, value) => protector.Protect(path, value));
        }

        // Written whole and renamed into place, never streamed over the live file — a rename is atomic, so
        // a crash mid-write never leaves a half file (2026-07-14 incident). Previous version kept as .bak,
        // which is what ReadAsync falls back to; owner-only, since this file holds provider credentials.
        CockpitConfigPath.ReplaceAtomicallyPrivate(configFilePath, document.ToJsonString(SerializerOptions));

        // AC-41: this universal seam is where a credential written in the clear becomes visible to the
        // awareness banner — only while encryption is off and only when this save carries a credential.
        if (protector is null
            && SecretJsonWalker.Transform(document.DeepClone(), _keyHolder.Fields, (_, value) => value).Count > 0)
        {
            _keyHolder.NoteUnprotectedSecretsWritten();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // AC-41: the write gate lives in CockpitConfigWriteGate so the encryption migration and the banner
    // dismissal share this lock. Reads don't take it, but a reader mid-swap waits the writer out instead.
}
