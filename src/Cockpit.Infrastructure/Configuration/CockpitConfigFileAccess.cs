using System.Collections.Concurrent;
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
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // The last version that read cleanly, written by every save — and what a damaged config is recovered from.
    private const string BackupSuffix = ".bak";

    // How long a read waits out a writer holding the file. A swap is milliseconds; reaching this means something other than contention.
    private static readonly TimeSpan ReadContentionWindow = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReadContentionInterval = TimeSpan.FromMilliseconds(20);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // AC-1892: one decrypted snapshot per config file for the whole process, whichever store's instance read it.
    private static readonly ConcurrentDictionary<string, ConfigSnapshot> Snapshots =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly ISecretKeyHolder _keyHolder = keyHolder ?? SecretKeyHolder.Shared;

    // AC-1343: what UpdateAsync chains non-batch writes behind, FIFO, so FlushAsync can wait out every one
    // still in flight — see UpdateAsync's own comment for why that beats trusting the write gate's poll order.
    private readonly Lock _tailLock = new();
    private Task _tail = Task.CompletedTask;

    // AC-1510: said after every save of a config file, with its path, so the settings sync can push a change.
    internal static event Action<string>? Written;

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

            // Written outside the gate and with the backup's own timestamp, so the damaged snapshot goes by hand.
            Snapshots.TryRemove(Path.GetFullPath(configFilePath), out _);

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

    // AC-1892: every read hands out its own fresh DTO (callers mutate what they get), but from a snapshot that is
    // read and decrypted once per write generation. Generation and stat are taken before reading, so a write that
    // lands mid-read, here or from another process, leaves a snapshot that no longer matches rather than a stale hit.
    private async Task<CockpitConfigFile?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        var generation = CockpitConfigWriteGate.Generation;
        var protector = _keyHolder.Protector;
        var fields = _keyHolder.Fields;
        var key = Path.GetFullPath(path);
        try
        {
            if (!Snapshots.TryGetValue(key, out var snapshot) || !snapshot.Matches(generation, file, protector, fields))
            {
                var json = await ReadPlaintextAsync(path, protector, fields, cancellationToken).ConfigureAwait(false);
                snapshot = new ConfigSnapshot(generation, file.LastWriteTimeUtc, file.Length, protector, fields, json);
                Snapshots[key] = snapshot;
            }

            return JsonSerializer.Deserialize<CockpitConfigFile>(snapshot.Json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The file as plaintext JSON: its own bytes while there is nothing to decrypt (AC-1152: no JsonNode tree just
    // to walk), else with every credential decrypted. Held only in Snapshots; never logged or handed out as is.
    private static Task<byte[]> ReadPlaintextAsync(
        string path,
        ISecretProtector? protector,
        SecretFields fields,
        CancellationToken cancellationToken)
    {
        CockpitConfigReads.NoteParse();
        return ReadFileWhenNotBeingReplacedAsync(path, ReadPlaintext, cancellationToken);

        async ValueTask<byte[]> ReadPlaintext(Stream stream)
        {
            if (protector is null)
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                var bytes = copy.ToArray();
                return bytes.AsSpan().StartsWith(Utf8Bom) ? bytes[Utf8Bom.Length..] : bytes;
            }

            var document = JsonNode.Parse(stream);
            if (document is not null)
            {
                SecretJsonWalker.Transform(document, fields, (path, value) =>
                    SecretProtector.IsProtected(value) ? protector.Unprotect(path, value) : null);
            }

            return JsonSerializer.SerializeToUtf8Bytes(document);
        }
    }

    // Keyed by full path; the protector and fields are part of the match, so unlocking, a password change or a
    // newly declared secret field never sees a snapshot decrypted (or not) under another key.
    private sealed class ConfigSnapshot(
        long generation,
        DateTime lastWriteUtc,
        long length,
        ISecretProtector? protector,
        SecretFields fields,
        byte[] json)
    {
        public byte[] Json => json;

        public bool Matches(long currentGeneration, FileInfo file, ISecretProtector? currentProtector, SecretFields currentFields) =>
            generation == currentGeneration
            && lastWriteUtc == file.LastWriteTimeUtc
            && length == file.Length
            && ReferenceEquals(protector, currentProtector)
            && ReferenceEquals(fields, currentFields);
    }

    // Reads the file, waiting out a writer mid-swap (`File.Replace` holds the destination and a reader
    // that lands there gets a sharing violation) rather than failing outright — the 2026-07-15 incident.
    // Internal so `SecretProtectionService` (review #9) reads through the same retry.
    internal static Task<string> ReadWhenNotBeingReplacedAsync(string path, CancellationToken cancellationToken) =>
        WhenNotBeingReplacedAsync(() => File.ReadAllTextAsync(path, cancellationToken), cancellationToken);

    // AC-1487: for a process that must never write here (`--remote`): the bytes once, shared with every writer so a save
    // can still swap the file, and no recovery. Null without a file; a document that does not parse throws instead.
    internal static async Task<JsonNode?> ReadOnceAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await WhenNotBeingReplacedAsync(ReadAllAsync, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(bytes);

        async Task<byte[]> ReadAllAsync()
        {
            using var copy = new MemoryStream();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            return copy.ToArray();
        }
    }

    // The typed file from a `ReadOnceAsync` document; credentials are decrypted in memory only once `keyHolder` is unlocked.
    internal static CockpitConfigFile? Decode(JsonNode document, ISecretKeyHolder keyHolder)
    {
        var copy = document.DeepClone();
        if (keyHolder.Protector is { } protector)
        {
            SecretJsonWalker.Transform(copy, keyHolder.Fields, (path, value) =>
                SecretProtector.IsProtected(value) ? protector.Unprotect(path, value) : null);
        }

        return copy.Deserialize<CockpitConfigFile>(SerializerOptions);
    }

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
            // FileShare.Read made a reader block the restore's writer; readers must let writers (and replacers) through.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return await read(stream).ConfigureAwait(false);
        }
    }

    internal static async Task<T> WhenNotBeingReplacedAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
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
    public Task UpdateAsync(Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        if (CockpitConfigWriteBatch.TryApply(this, mutate, cancellationToken, out var applied))
        {
            return applied;
        }

        // AC-1343: dispatched onto the thread pool and chained FIFO behind whatever UpdateAsync queued before it
        // — never run inline, so the task this returns (what FlushAsync waits on) exists the instant this method
        // returns, not only once an uncontended write happens to finish synchronously first.
        lock (_tailLock)
        {
            var previous = _tail;
            return _tail = Task.Run(() => _ChainedUpdateAsync(previous, mutate, cancellationToken));
        }
    }

    private async Task _ChainedUpdateAsync(Task previous, Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A prior write's failure is that write's own caller's problem to observe — it must not block this one.
        }

        await UpdateNowAsync(mutate, cancellationToken).ConfigureAwait(false);
    }

    // AC-1343: what a plugin store's IAsyncDisposable calls before the container releases it on exit, so a
    // SaveDataAsync/DeclareAsync fired as `_ = ...` (IPluginStorage.Set is a void callback) lands before the
    // process does. Non-batch writes only — CockpitConfigWriteBatch.TryApply never touches _tail.
    internal Task FlushAsync()
    {
        lock (_tailLock)
        {
            return _tail;
        }
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
        ManagedConfigKeys.Enforce(configFile);
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

        Written?.Invoke(configFilePath);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // What TryPeek hands a mid-batch ReadAsync caller instead of the live, still-mutating batch document — so
    // mutating what it got back (nothing does today, but ReadAsync's contract allows it) can't corrupt the batch.
    internal static CockpitConfigFile ClonePeeked(CockpitConfigFile configFile) =>
        JsonSerializer.Deserialize<CockpitConfigFile>(JsonSerializer.Serialize(configFile, SerializerOptions), SerializerOptions)!;

    // AC-41: the write gate lives in CockpitConfigWriteGate so the encryption migration and the banner
    // dismissal share this lock. Reads wait while it is held, so they do not stack up behind a writer.
}
