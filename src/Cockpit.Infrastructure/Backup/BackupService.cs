using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Secrets;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Backup;

// Makes and restores a backup of the whole cockpit (#70) — one zip, one manifest. A restore is destructive
// and therefore all-or-nothing: the archive is unpacked to a temp directory and read there, and only when
// it's sound does anything on disk move, so a restore that dies halfway leaves you with what you had.
internal sealed class BackupService(
    ISessionProfileStore profiles,
    ILogger<BackupService> logger) : IBackupService, ISingletonService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string CockpitDirectory => CockpitConfigPath.Root;

    // Backup and restore stage under the cockpit's own state root, never Path.GetTempPath() (AC-45): on Linux
    // that is a world-readable 1777 /tmp, and a restore unpacks the whole archive — every credential — there
    // for its duration. Staged here, in an owner-only directory, that window is not readable by other users.
    internal static string StagingRoot => Path.Combine(CockpitConfigPath.Root, BackupContents.StagingFolder);

    // Offloaded to the thread pool (AC-747): CreateEntryFromFile has no async form, so archiving every file froze
    // whichever thread called this — the UI thread, in practice. The awaiter still marshals the continuation back
    // to the caller's dispatcher, so BackupStatus updates land on the UI thread exactly as before.
    public Task<BackupManifest> WriteAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken = default) =>
        Task.Run(() => _WriteCoreAsync(archivePath, options, cancellationToken), cancellationToken);

    private async Task<BackupManifest> _WriteCoreAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken)
    {
        var root = CockpitDirectory;
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException("There is nothing to back up: this cockpit has never saved anything.");
        }

        var removed = new List<string>();
        var profileDirectories = new Dictionary<string, string>(StringComparer.Ordinal);

        // Written to a temporary file and moved into place: a half-written archive with the right name is a backup
        // you will trust exactly once. Under the owner-only staging root (AC-45), so the credential-bearing zip is
        // never briefly readable to other users while it is being built.
        CockpitConfigPath.EnsurePrivateDirectory(StagingRoot);
        var staging = Path.Combine(StagingRoot, $"cockpit-backup-{Guid.NewGuid():n}.zip");

        try
        {
            using (var archive = ZipFile.Open(staging, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(root, file);
                    if (!BackupContents.Includes(relative))
                    {
                        continue;
                    }

                    var entryName = $"cockpit/{relative.Replace('\\', '/')}";

                    // The settings are the one file that is rewritten on the way in: secrets out, unless asked for.
                    if (string.Equals(relative, "cockpit.json", StringComparison.OrdinalIgnoreCase))
                    {
                        removed.AddRange(await _WriteSettingsAsync(archive, entryName, file, options, cancellationToken));
                        continue;
                    }

                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }

                if (options.IncludeProfileConfigs)
                {
                    profileDirectories = await _WriteProfileConfigsAsync(archive, cancellationToken);
                }

                var manifest = new BackupManifest(
                    BackupManifest.CurrentSchema,
                    typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                    DateTimeOffset.UtcNow,
                    options.IncludeCredentials,
                    removed,
                    profileDirectories,
                    _PluginsIn(root, options),
                    root);

                var entry = archive.CreateEntry(BackupManifest.FileName, CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await JsonSerializer.SerializeAsync(stream, manifest, Json, cancellationToken);
            }

            await MoveIntoPlaceAsync(staging, archivePath, cancellationToken);

            logger.LogInformation(
                "Wrote a backup to {Path} ({Credentials}, {Secrets} secret(s) stripped)",
                archivePath,
                options.IncludeCredentials ? "with credentials" : "without credentials",
                removed.Count);

            return await ReadManifestAsync(archivePath, cancellationToken);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    // How long the move waits out whatever is holding one of the two files. Past this it is not contention.
    private static readonly TimeSpan MoveContentionWindow = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MoveContentionInterval = TimeSpan.FromMilliseconds(100);

    // Puts the finished archive where the operator asked for it, waiting out a file that is briefly busy —
    // a fresh .zip is exactly what a virus scanner opens right after it closes. A held source and a held
    // destination fail differently, so the refusal can say which is stuck. `contentionWindow` is overridable.
    internal static async Task MoveIntoPlaceAsync(
        string staging,
        string archivePath,
        CancellationToken cancellationToken,
        TimeSpan? contentionWindow = null)
    {
        var deadline = DateTimeOffset.UtcNow + (contentionWindow ?? MoveContentionWindow);
        while (true)
        {
            try
            {
                File.Move(staging, archivePath, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(MoveContentionInterval, cancellationToken);
                    continue;
                }

                throw new IOException($"The backup could not be put in place: {_WhatIsStuck(exception, archivePath)}", exception);
            }
        }
    }

    private static string _WhatIsStuck(Exception exception, string archivePath) =>
        exception is UnauthorizedAccessException
            ? $"'{archivePath}' could not be written. It is open in another program, or you do not have permission "
              + "to write there. Close anything using it, or pick another location, and try again."
            : "the finished archive is still held by something else — a virus scanner unpacking a newly written "
              + ".zip is the usual culprit. Try again in a moment.";

    public async Task<BackupManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var entry = archive.GetEntry(BackupManifest.FileName)
            ?? throw new InvalidOperationException("This zip is not a cockpit backup: it has no backup.json.");

        await using var stream = entry.Open();

        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, Json, cancellationToken)
            ?? throw new InvalidOperationException("This backup's manifest could not be read.");
    }

    // Same offload as WriteAsync, and for the same reason (AC-747): ZipFile.ExtractToDirectory unpacks the whole
    // archive synchronously, which blocked the UI thread for as long as the restore took.
    public Task<RestoreReport> RestoreAsync(
        string archivePath,
        RestoreOptions options,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RestoreIntoAsync(archivePath, CockpitDirectory, options, progress, cancellationToken), cancellationToken);

    // The root is a parameter because a restore into the operator's real cockpit is not something a test may do.
    // A stop is REPORTED, not thrown, against the usual reading of a CancellationToken (AC-1281): stopping leaves
    // whatever already landed standing, and naming what did not is exactly what an exception cannot carry.
    internal async Task<RestoreReport> RestoreIntoAsync(
        string archivePath,
        string root,
        RestoreOptions options,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(archivePath, cancellationToken);

        if (manifest.RestoreRefusal is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        if (!options.Settings && options.Plugins.Count == 0)
        {
            throw new InvalidOperationException("Nothing was selected, so nothing was restored.");
        }

        // Unpack first, write second. Everything that can fail — a corrupt entry, a full disk — fails while this
        // cockpit is still untouched. Extracted into an owner-only directory (AC-45): the archive holds every
        // credential, and the extraction window must not expose them to other users the way a shared /tmp would.
        var staging = Path.Combine(root, BackupContents.StagingFolder, $"cockpit-restore-{Guid.NewGuid():n}");

        try
        {
            progress?.Report(new RestoreProgress(RestoreStage.Unpacking));
            CockpitConfigPath.EnsurePrivateDirectory(staging);
            ZipFile.ExtractToDirectory(archivePath, staging);

            var archived = Path.Combine(staging, "cockpit");
            if (!Directory.Exists(archived))
            {
                throw new InvalidOperationException("This backup carries no cockpit directory, so there is nothing to restore.");
            }

            Directory.CreateDirectory(root);

            // What is being replaced is set aside, never deleted: a restore is the one act here that can cost someone
            // a day, and "it is still there, under this name" is the difference between a mistake and a disaster.
            var aside = Path.Combine(Path.GetDirectoryName(root)!, $"{Path.GetFileName(root)}.replaced-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");

            // The first of the two places a stop is honoured. Checked here rather than only at the hard line below,
            // so a stop does not first sit out the fetch — the step that takes minutes. Nothing outside staging has
            // been touched, so there is nothing to name: an empty report, and the `finally` clears staging.
            if (cancellationToken.IsCancellationRequested)
            {
                return new RestoreReport(Stopped: true, []);
            }

            // AC-1279 fetches the binaries here, honouring the token between plugins; until it does, the count runs
            // straight to the end and every chosen plugin reads as still missing — which is what is true today.
            progress?.Report(new RestoreProgress(RestoreStage.FetchingPlugins, options.Plugins.Count, options.Plugins.Count));

            // The line the restore crosses once (AC-1281 moved it from "before writing" to "before cockpit.json"):
            // past here the token is deliberately not passed on. A stop asked for during the fetch returns rather
            // than throws — what landed stays, so naming what did not is the whole point of reporting back.
            var stopped = cancellationToken.IsCancellationRequested;

            var missing = _PluginsWithoutBinaries(root, options, stopped
                ? "the restore was stopped before it was fetched"
                : "its binaries were not fetched from its store");

            if (stopped)
            {
                logger.LogInformation(
                    "Restore from {Path} stopped before the settings were written; {Plugins} plugin(s) are not installed",
                    archivePath,
                    missing.Count);

                return new RestoreReport(Stopped: true, missing);
            }

            progress?.Report(new RestoreProgress(RestoreStage.Writing));

            await _RestoreSettingsAsync(root, archived, aside, options, CancellationToken.None);

            // AC-695: after the merge on purpose, and safe there even though which value came from the archive can no
            // longer be seen — restoring onto the same machine makes source and target root equal and the rewrite a
            // no-op, and onto another machine this cockpit's own values do not start with the archive's foreign root.
            await _RebaseRestoredPathsAsync(root, manifest);

            if (options.Settings)
            {
                _RestoreLooseFiles(root, archived, aside);
                _RestoreProfileConfigs(staging, manifest, options);
            }

            logger.LogInformation(
                "Restored from {Path}: {Settings}, {Plugins} plugin(s). What was replaced is at {Aside}",
                archivePath,
                options.Settings ? "settings" : "no settings",
                options.Plugins.Count,
                aside);

            return new RestoreReport(Stopped: false, missing);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    // cockpit.json is one file holding two things the operator restores separately: the cockpit's own settings, and
    // each plugin's registration (which carries everything that plugin ever stored). So it is merged, key by key,
    // rather than swapped — restoring one plugin must not silently bring back yesterday's profiles with it.
    private static async Task _RestoreSettingsAsync(string root, string archived, string aside, RestoreOptions options, CancellationToken cancellationToken)
    {
        var archivedFile = Path.Combine(archived, "cockpit.json");
        if (!File.Exists(archivedFile))
        {
            return;
        }

        var incoming = JsonNode.Parse(await File.ReadAllTextAsync(archivedFile, cancellationToken)) as JsonObject
            ?? throw new InvalidOperationException("The cockpit.json in this backup could not be read, so nothing was restored.");

        var currentFile = Path.Combine(root, "cockpit.json");
        var current = File.Exists(currentFile)
            ? JsonNode.Parse(await File.ReadAllTextAsync(currentFile, cancellationToken)) as JsonObject ?? []
            : [];

        if (File.Exists(currentFile))
        {
            Directory.CreateDirectory(aside);
            File.Copy(currentFile, Path.Combine(aside, "cockpit.json"), overwrite: true);
        }

        var result = options.Settings ? _Without(incoming, "Plugins") : _Without(current, "Plugins");

        // The plugins section: whichever plugins were chosen come from the archive, the rest stay exactly as they are.
        // Nothing here looks on disk — a plugin whose binaries are not back yet keeps its settings all the same (AC-1278).
        var plugins = current["Plugins"] as JsonObject ?? [];
        var restoredPlugins = new JsonObject();

        foreach (var (id, registration) in plugins)
        {
            restoredPlugins[id] = registration?.DeepClone();
        }

        if (incoming["Plugins"] is JsonObject incomingPlugins)
        {
            foreach (var (id, registration) in incomingPlugins)
            {
                if (options.Includes(id))
                {
                    restoredPlugins[id] = registration?.DeepClone();
                }
            }
        }

        result["Plugins"] = restoredPlugins;

        await File.WriteAllTextAsync(currentFile, result.ToJsonString(Json), cancellationToken);
    }

    // AC-695: the merged file carries the backup machine's own absolute paths — a `D:\` on a machine that has no
    // D:. Run over the result rather than over the archive so it covers the settings and the plugin registrations
    // in one pass, whichever of the two this restore took from the archive.
    private async Task _RebaseRestoredPathsAsync(string root, BackupManifest manifest)
    {
        var file = Path.Combine(root, "cockpit.json");
        if (!File.Exists(file) || JsonNode.Parse(await File.ReadAllTextAsync(file)) is not JsonObject settings)
        {
            return;
        }

        var unresolved = RestorePathPortability.Rebase(settings, manifest.SourceConfigRoot, root);
        await File.WriteAllTextAsync(file, settings.ToJsonString(Json));

        if (unresolved.Count > 0)
        {
            logger.LogWarning(
                "{Count} project folder(s) from this backup do not exist here and were left in the settings as they "
                + "are, rather than being dropped or pointed somewhere else: {Folders}. Set each one again.",
                unresolved.Count,
                string.Join("; ", unresolved));
        }
    }

    private static JsonObject _Without(JsonObject source, string key)
    {
        var copy = new JsonObject();

        foreach (var (name, value) in source)
        {
            if (!string.Equals(name, key, StringComparison.Ordinal))
            {
                copy[name] = value?.DeepClone();
            }
        }

        return copy;
    }

    // Since AC-1276 an archive carries no plugin binaries: a restored plugin has its settings back and no code
    // until it is fetched from its store again. Returned as well as logged (AC-1281), because a log line is not
    // something the operator reads, and "the folder was not in the archive" then looks exactly like success.
    private IReadOnlyList<RestoreMissingPlugin> _PluginsWithoutBinaries(string root, RestoreOptions options, string reason)
    {
        var missing = options.Plugins
            .Where(id => !Directory.Exists(Path.Combine(root, "plugins", id)))
            .Select(id => new RestoreMissingPlugin(id, reason))
            .ToList();

        if (missing.Count > 0)
        {
            logger.LogWarning(
                "Restored the settings of {Count} plugin(s) that are not installed here: {Plugins}. "
                + "They stay unusable until their binaries are fetched from their store again.",
                missing.Count,
                string.Join(", ", missing.Select(plugin => plugin.Id)));
        }

        return missing;
    }

    // Everything the archive carries besides cockpit.json: the MCP permissions, the assistant's memory and state,
    // the project logos. Walked recursively out of staging rather than off a list of names, so what a backup
    // includes stays one decision, made in `BackupContents.Included`.
    private static void _RestoreLooseFiles(string root, string archived, string aside)
    {
        foreach (var file in Directory.EnumerateFiles(archived, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(archived, file);
            if (relative.Equals("cockpit.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(root, relative);

            if (File.Exists(target))
            {
                var kept = Path.Combine(aside, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Copy(target, kept, overwrite: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // The profiles' own config directories, when the backup carries them: put back where the manifest says they lived,
    // and whatever is there now moved aside rather than merged — two half-merged agent logins is a state nobody could
    // reason about afterwards.
    private static void _RestoreProfileConfigs(string staging, BackupManifest manifest, RestoreOptions options)
    {
        foreach (var (label, directory) in manifest.ProfileConfigDirectories)
        {
            var source = Path.Combine(staging, "profiles", label);
            if (!Directory.Exists(source))
            {
                continue;
            }

            if (Directory.Exists(directory))
            {
                Directory.Move(directory, $"{directory}.replaced-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            Directory.Move(source, directory);
        }
    }

    private static async Task<IReadOnlyList<string>> _WriteSettingsAsync(
        ZipArchive archive,
        string entryName,
        string file,
        BackupOptions options,
        CancellationToken cancellationToken)
    {
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken))
            ?? throw new InvalidOperationException("cockpit.json could not be read, so the backup would not have been one.");

        // AC-1277: leaving a plugin out no longer strips its registration here. That registration — the menu, and
        // the plugin's own `Data` — used to travel with the binaries, so dropping one dropped the other; with no
        // binaries in the archive it is the content. The choice now only governs the manifest's plugin list.

        // The plugins' own declared fields too (a "pat", a "credential"), not just the names the host recognises:
        // an archive that says it carries no credentials must carry none, and a field the encryption protects but
        // the scrubber misses is a token in a backup that claims to be safe to store anywhere.
        var removed = options.IncludeCredentials
            ? []
            : SecretScrubber.Scrub(settings, SecretKeyHolder.Shared.Fields);

        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(settings.ToJsonString(Json)), cancellationToken);

        return removed;
    }

    // The plugins the archive asks a restore to fetch back, read off the folders. Not a `BackupPluginIndexEntry`:
    // no plugin records which store it came from, and fetching every store's index here would let an expired token
    // drop one in silence. AC-1279 resolves the store at restore, by id, version and the registration's `PinnedSha256`.
    private static Dictionary<string, string> _PluginsIn(string root, BackupOptions options)
    {
        var plugins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(root, "plugins");

        if (!Directory.Exists(directory))
        {
            return plugins;
        }

        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var id = Path.GetFileName(folder);
            if (!options.Includes(id))
            {
                continue;
            }

            plugins[id] = _VersionOf(folder);
        }

        return plugins;
    }

    private static string _VersionOf(string folder)
    {
        try
        {
            var manifest = Path.Combine(folder, "plugin.json");

            return File.Exists(manifest) && JsonNode.Parse(File.ReadAllText(manifest))?["version"]?.ToString() is { Length: > 0 } version
                ? version
                : "unknown";
        }
        catch (JsonException)
        {
            // A plugin whose manifest we cannot read is still listed; only its version line is a shrug.
            return "unknown";
        }
    }

    private async Task<Dictionary<string, string>> _WriteProfileConfigsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var profile in await profiles.LoadAsync(cancellationToken))
        {
            // A profile running under another provider has no config directory to back up here — only the
            // Claude CLI's own credentials/config live on disk under a profile-pinned directory.
            if (profile.Claude is not { ConfigDir: { } configDir } || !Directory.Exists(configDir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(configDir, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, $"profiles/{profile.Label}/{relative}", CompressionLevel.Optimal);
            }

            written[profile.Label] = configDir;
        }

        return written;
    }
}
