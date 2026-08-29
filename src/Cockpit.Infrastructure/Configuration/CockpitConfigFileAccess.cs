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

    // AC-1108: mid-batch, sees that batch's own not-yet-flushed copy instead of stale disk — GlobalHotkeyCoordinator
    // re-reads a section a sibling save just wrote, from the VoiceSettingsSaved handler that save raises mid-Apply.
    public async Task<CockpitConfigFile?> ReadAsync(CancellationToken cancellationToken) =>
        CockpitConfigWriteBatch.TryPeek(this) is { } pending
            ? pending
            : await ReadNowAsync(cancellationToken).ConfigureAwait(false);

    // The real disk read — what ReadAsync does outside a batch, and what seeds a batch's first mutation.
    internal async Task<CockpitConfigFile?> ReadNowAsync(CancellationToken cancellationToken, bool waitForWriter = true)
    {
        if (waitForWriter)
        {
            await CockpitConfigWriteGate.WaitForWriterAsync(configFilePath, cancellationToken).ConfigureAwait(false);
        }

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
            // AC-1152: with nothing to decrypt there is nothing to walk, so the file goes straight into the DTO
            // without a JsonNode tree of the whole document in between — the tree exists only to be walked.
            if (_keyHolder.Protector is not { } protector)
            {
                return await ReadFileWhenNotBeingReplacedAsync(
                    path,
                    stream => JsonSerializer.DeserializeAsync<CockpitConfigFile>(stream, SerializerOptions, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            var document = await ReadFileWhenNotBeingReplacedAsync(
                path,
                stream => ValueTask.FromResult(JsonNode.Parse(stream)),
                cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                return null;
            }

            SecretJsonWalker.Transform(document, _keyHolder.Fields, (path, value) =>
                SecretProtector.IsProtected(value) ? protector.Unprotect(path, value) : null);

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
    internal static Task<string> ReadWhenNotBeingReplacedAsync(string path, CancellationToken cancellationToken) =>
        WhenNotBeingReplacedAsync(() => File.ReadAllTextAsync(path, cancellationToken), cancellationToken);

    // AC-1152: hands `read` the file itself rather than a string of the whole document. Above 85 kB that string
    // is a large object of its own, on every read — and `AllocLarge` was the measured reason for most gen2 collections.
    private static Task<T> ReadFileWhenNotBeingReplacedAsync<T>(
        string path,
        Func<Stream, ValueTask<T>> read,
        CancellationToken cancellationToken)
    {
        return WhenNotBeingReplacedAsync(OpenAndReadAsync, cancellationToken);

        async Task<T> OpenAndReadAsync()
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            return await read(stream).ConfigureAwait(false);
        }
    }

    private static async Task<T> WhenNotBeingReplacedAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ReadContentionWindow;
        while (true)
        {
            try
            {
                return await read().ConfigureAwait(false);
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

    // Loads the file, mutates one section, and writes the whole document back, serialised against every other
    // writer on this machine (how a plugin's freshly pinned hash once disappeared) — or, AC-1108, joins the
    // ambient CockpitConfigWriteBatch when one is open instead of writing immediately.
    public async Task UpdateAsync(Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        if (CockpitConfigWriteBatch.TryApply(this, mutate, cancellationToken, out var applied))
        {
            await applied.ConfigureAwait(false);
            return;
        }

        await UpdateNowAsync(mutate, cancellationToken).ConfigureAwait(false);
    }

    // What UpdateAsync does outside a batch — never consults the ambient batch itself, or its own flush would
    // re-enqueue into itself.
    internal async Task UpdateNowAsync(Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(configFilePath, cancellationToken).ConfigureAwait(false);

        var configFile = await ReadNowAsync(cancellationToken, waitForWriter: false).ConfigureAwait(false) ?? new CockpitConfigFile();
        mutate(configFile);

        await WriteNowAsync(configFile).ConfigureAwait(false);
    }

    // Serialises and replaces the file with `configFile` as given — no read, no gate of its own: the caller
    // already holds one, UpdateNowAsync's own or a CockpitConfigWriteBatch's held across the whole scope.
    internal async Task WriteNowAsync(CockpitConfigFile configFile)
    {
        var document = JsonSerializer.SerializeToNode(configFile, SerializerOptions)
            ?? throw new InvalidOperationException("The cockpit configuration serialized to nothing.");

        var protector = _keyHolder.Protector;
        if (protector is not null)
        {
            SecretJsonWalker.Transform(document, _keyHolder.Fields, (path, value) => protector.Protect(path, value));
        }

        // Written to a sidecar and renamed into place, never over the live file — a rename is atomic, so a crash
        // mid-write never leaves a half file (2026-07-14 incident); .bak is what ReadAsync falls back to, owner-only.
        // AC-1152: serialised into that sidecar's stream, not first into a string of the whole document.
        CockpitConfigPath.ReplaceAtomicallyPrivate(configFilePath, stream =>
        {
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            document.WriteTo(writer, SerializerOptions);
        });

        // AC-41: this universal seam is where a credential written in the clear becomes visible to the
        // awareness banner — only while encryption is off and only when this save carries a credential.
        if (protector is null && SecretJsonWalker.ContainsSecret(document, _keyHolder.Fields))
        {
            _keyHolder.NoteUnprotectedSecretsWritten();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // What TryPeek hands a mid-batch ReadAsync caller instead of the live, still-mutating batch document — so
    // mutating what it got back (nothing does today, but ReadAsync's contract allows it) can't corrupt the batch.
    internal static CockpitConfigFile ClonePeeked(CockpitConfigFile configFile) =>
        JsonSerializer.Deserialize<CockpitConfigFile>(JsonSerializer.Serialize(configFile, SerializerOptions), SerializerOptions)!;

    // AC-41: the write gate lives in CockpitConfigWriteGate so the encryption migration and the banner
    // dismissal share this lock. Reads wait for it before opening the config, so a writer cannot starve.
}
