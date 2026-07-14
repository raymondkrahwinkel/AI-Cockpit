using System.Collections.Concurrent;
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

    /// <summary>
    /// One gate per config file, shared by every instance of this class — the seventeen stores each build their
    /// own, and they all write the same file. Static because the writers are not each other's dependencies and
    /// never meet: the file is what they share, so the file is what the lock is keyed on.
    /// <para>
    /// Within one process this makes the read-modify-write indivisible. Across processes it does not, and cannot:
    /// a second cockpit on the same config still overwrites, last save wins. That is a loss of a change, never a
    /// broken file — each save is a whole document renamed into place from a sidecar of its own.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

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
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
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
    /// Loads the current file (or a fresh, empty one), applies <paramref name="mutate"/> to a single
    /// section, and writes the whole document back — preserving every other section.
    /// </summary>
    public async Task UpdateAsync(Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        // Read, mutate and write as one indivisible step. Seventeen stores each construct their own instance of
        // this class over the same file, so "read the whole document, change my section, write it all back" runs
        // concurrently with itself — and without this gate the second reader starts from a document that predates
        // the first writer, then writes its stale copy over the top. The section the first store just saved is
        // simply gone, with nothing to show for it. The gate is per file and shared by every instance.
        var gate = Gates.GetOrAdd(Path.GetFullPath(configFilePath), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _UpdateUnderGateAsync(mutate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task _UpdateUnderGateAsync(Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        var configFile = await ReadAsync(cancellationToken).ConfigureAwait(false) ?? new CockpitConfigFile();
        mutate(configFile);

        var document = JsonSerializer.SerializeToNode(configFile, SerializerOptions)
            ?? throw new InvalidOperationException("The cockpit configuration serialized to nothing.");

        if (_keyHolder.Protector is { } protector)
        {
            SecretJsonWalker.Transform(document, _keyHolder.Fields, (path, value) => protector.Protect(path, value));
        }

        // Written whole and renamed into place, never streamed over the live file.
        //
        // Truncating the config and then streaming the new one into it means that for the length of that write the
        // operator's settings exist nowhere: a crash, a kill or the power going leaves a half file — and the next
        // start, finding it unreadable, would have begun with an empty config and saved that emptiness over
        // everything.
        //
        // The "two writers" that damaged the operator's config on 2026-07-14 were not a second instance or a script,
        // as this comment used to guess: they were two of our own stores, in this process, sharing one fixed
        // "<path>.new" sidecar. Both fixes live where the fault was — the gate above, and a sidecar per write.
        //
        // A rename is atomic: the file is either entirely the old one or entirely the new one. The previous version
        // is kept as .bak, which is what ReadAsync falls back to. Owner-only either way — this file holds provider
        // API keys, MCP bearer headers and the plugins' tokens.
        CockpitConfigPath.ReplaceAtomicallyPrivate(configFilePath, document.ToJsonString(SerializerOptions));

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
