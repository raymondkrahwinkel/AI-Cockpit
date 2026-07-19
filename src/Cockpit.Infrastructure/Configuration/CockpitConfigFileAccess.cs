using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Shared read-modify-write access to the single <c>cockpit.json</c> file. Both the profile store
/// and the notification store go through this so each can update its own section without clobbering
/// the other's: they always load the full <see cref="CockpitConfigFile"/>, mutate one section, and
/// write the whole file back.
/// <para>
/// It is also where the credentials are encrypted and decrypted. Every section — profiles, MCP servers,
/// notifications, and the plugins' own storage — passes through here, so hanging the protection under this one
/// seam covers all of them, and covers a plugin that has never heard of it. Encryption is off unless the
/// operator turned it on and unlocked the app, in which case <see cref="ISecretKeyHolder.Protector"/> holds the
/// key for as long as the process runs.
/// </para>
/// </summary>
internal sealed class CockpitConfigFileAccess(string configFilePath, ISecretKeyHolder? keyHolder = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The last version that read cleanly, written by every save — and what a damaged config is recovered from.</summary>
    private const string BackupSuffix = ".bak";

    /// <summary>How long a read waits out a writer holding the file. A swap is milliseconds; reaching this means something other than contention.</summary>
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

        // The file exists and does not parse. There is no honest way to read on: an operator's settings are not a
        // thing to guess at, and the danger is not the failed read but what comes after it — a caller that treats
        // an unreadable config as an absent one starts with an empty document and writes that emptiness back over
        // everything on the next save. So the last known-good copy (kept by every write) is tried first, and only a
        // genuinely missing file returns null.
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

    /// <summary>
    /// Reads the file, waiting out the moment a writer has it. <see cref="File.Replace(string,string,string)"/>
    /// holds the destination for the length of the swap, and a reader that lands in that window gets a sharing
    /// violation rather than either version of the file.
    /// </summary>
    /// <remarks>
    /// This is what "a rename is atomic, so a reader never waits on a writer" missed: the <em>content</em> a
    /// reader sees is indeed all-or-nothing, but the read itself can still fail outright. On 2026-07-15 it did —
    /// at startup, where several stores read this file while the plugin layer wrote it — and the callers that did
    /// not catch it lost what they were starting. Global push-to-talk was one, and it went silently.
    /// <para>
    /// Waiting is right where refusing is not: the file is there, it is readable, and it is busy for the length of
    /// one swap. Past the window something else is wrong, so the exception goes on to <see cref="ReadAsync"/>'s
    /// recovery — the backup, and then a refusal that says so.
    /// </para>
    /// <para>
    /// Internal rather than private so <see cref="SecretProtectionService"/> reads through the same retry (review
    /// #9): its status probe and migrations touch the same file, and an ungated read there had the same race.
    /// </para>
    /// </remarks>
    internal static async Task<string> ReadWhenNotBeingReplacedAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ReadContentionWindow;
        while (true)
        {
            try
            {
                return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (exception is not FileNotFoundException
                                                && exception is not DirectoryNotFoundException
                                                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(ReadContentionInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Loads the current file (or a fresh, empty one), applies <paramref name="mutate"/> to a single
    /// section, and writes the whole document back — preserving every other section.
    /// <para>
    /// Serialised against every other writer, in this process and in any other cockpit on this machine. It has
    /// to be: "preserving every other section" is only true if nothing changed a section between the read and
    /// the write. Without the gate, two writers each read the file, each changed their own section, and each
    /// wrote the whole document — so the one that finished last silently restored the other's section to what
    /// it had been. That is how a plugin's freshly pinned hash disappeared and the plugin came back asking for
    /// consent, and it is why writing atomically was never enough: each write was whole, and one of them was
    /// whole and stale.
    /// </para>
    /// </summary>
    public async Task UpdateAsync(Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        // Refuse to write while the app is locked with encryption still on (AC-5). Relock cleared the key without
        // turning encryption off, so there is no protector to encrypt the secret fields — yet every write serializes
        // the whole document, secret sections included. Writing now would land provider API keys, MCP bearer tokens
        // and plugin credentials on disk in the clear while the operator believes encryption is on and is looking at
        // the unlock screen. Fail closed: a background writer's change is lost (recoverable) rather than a secret
        // leaked (not). Writes resume the moment Unlock derives the key again.
        if (_keyHolder.IsLocked)
        {
            throw new InvalidOperationException(
                "Cockpit is locked; its configuration cannot be written until it is unlocked.");
        }

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

        // Written whole and renamed into place, never streamed over the live file.
        //
        // Truncating the config and then streaming the new one into it means that for the length of that write the
        // operator's settings exist nowhere: a crash, a kill or the power going leaves a half file — and the next
        // start, finding it unreadable, would have begun with an empty config and saved that emptiness over
        // everything. Two writers at once (a second instance, a script) could leave the tail of the longer document
        // behind the shorter one, which is what happened to Raymond's config on 2026-07-14.
        //
        // A rename is atomic: the file is either entirely the old one or entirely the new one. That makes each write
        // whole — it never made two writes safe, which is a different problem and the gate above's to solve.
        //
        // The previous version is kept as .bak, which is what ReadAsync falls back to. Owner-only either way — this
        // file holds provider API keys, MCP bearer headers and the plugins' tokens.
        CockpitConfigPath.ReplaceAtomicallyPrivate(configFilePath, document.ToJsonString(SerializerOptions));

        // Save-time signal (AC-41): this is the universal seam every section passes through, so it is where a
        // credential written in the clear — by a provider, an MCP server, a plugin this build never heard of —
        // becomes visible to the awareness banner. Only when encryption is off (nothing to warn about once it is
        // on) and only when this document actually carries a credential, so a settings save with no secret in it
        // never nudges the banner. In-memory: it counts on a clone and raises an event, never a second write.
        if (protector is null
            && SecretJsonWalker.Transform(document.DeepClone(), _keyHolder.Fields, (_, value) => value).Count > 0)
        {
            _keyHolder.NoteUnprotectedSecretsWritten();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // The write gate lives in CockpitConfigWriteGate now, so the encryption migration and the awareness-banner
    // dismissal (AC-41) take the same lock this does. Reads do not take it, but they are not free of it either:
    // the swap that publishes a write holds the file for its duration, so a reader that lands in that window is
    // refused rather than served either version — it waits the writer out instead (_ReadWhenNotBeingReplacedAsync).
}
