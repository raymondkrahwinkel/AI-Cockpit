using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Secrets;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Backup;

// Makes and restores a backup of the whole cockpit (#70) — one zip, one manifest. A restore is destructive
// and therefore all-or-nothing: the archive is unpacked to a temp directory and read there, and only when
// it's sound does anything on disk move, so a restore that dies halfway leaves you with what you had.
internal sealed class BackupService(
    ISessionProfileStore profiles,
    IPluginStoreConfigStore stores,
    IPluginStoreClient storeClient,
    IPluginProvisioningService provisioning,
    ILogger<BackupService> logger) : IBackupService, ISingletonService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // What a manifest goes in the archive as when its plugin.json could not be read — and, on the way back, the
    // one "version" a store is never asked for.
    private const string UnknownVersion = "unknown";

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

            // The first of the two places a stop is honoured, checked before the fetch so a stop does not first sit
            // out the step that takes minutes. Nothing outside staging is touched, so there is nothing to name and
            // nothing to re-anchor either (AC-695): that step hangs on settings being written, and they are not.
            if (cancellationToken.IsCancellationRequested)
            {
                return new RestoreReport(Stopped: true, []);
            }

            // Read on this side of the line: a cockpit.json the archive cannot hand back is the one remaining thing
            // that can fail, and failing here means it failed while this cockpit was still untouched.
            var incoming = await _ArchivedSettingsAsync(archived, cancellationToken);

            // The one thing past the line that may still be stopped (Raymond, AC-1279): fetching writes only whole
            // plugin folders, never `cockpit.json`, so a fetch cut short costs the restore and not the cockpit.
            var fetched = await _FetchPluginsAsync(root, incoming, manifest, options, progress, cancellationToken);

            // The line the restore crosses once (AC-1281 moved it from "before writing" to "before cockpit.json"):
            // past here the token is deliberately not passed on. A stop asked for during the fetch returns rather
            // than throws — what landed stays, so naming what did not is the whole point of reporting back.
            if (fetched.Stopped)
            {
                logger.LogInformation(
                    "Restore from {Path} stopped before the settings were written. {Landed} plugin(s) were installed "
                    + "and stay; {Noted} have something to say about them: {Plugins}",
                    archivePath,
                    fetched.Pinned.Count,
                    fetched.Notes.Count,
                    string.Join(", ", fetched.Notes.Select(plugin => $"{plugin.Id} ({plugin.Note})")));

                return new RestoreReport(Stopped: true, fetched.Notes);
            }

            progress?.Report(new RestoreProgress(RestoreStage.Writing));

            await _RestoreSettingsAsync(root, incoming, aside, options, fetched.Pinned, CancellationToken.None);

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

            return new RestoreReport(Stopped: false, fetched.Notes);
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
    private static async Task _RestoreSettingsAsync(
        string root,
        JsonObject? incoming,
        string aside,
        RestoreOptions options,
        IReadOnlyDictionary<string, string> pinned,
        CancellationToken cancellationToken)
    {
        if (incoming is null)
        {
            return;
        }

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

        // The one setting the archive does not get the last word on (AC-1279): a plugin pinned to a build other than
        // the one it came back on is dropped to needs-consent at the next start, so what landed wins.
        foreach (var (id, sha256) in pinned)
        {
            if (restoredPlugins[id] is JsonObject registration)
            {
                registration["PinnedSha256"] = sha256;
            }
            else
            {
                restoredPlugins[id] = new JsonObject { ["Enabled"] = true, ["PinnedSha256"] = sha256 };
            }
        }

        result["Plugins"] = restoredPlugins;

        await File.WriteAllTextAsync(currentFile, result.ToJsonString(Json), cancellationToken);
    }

    private static async Task<JsonObject?> _ArchivedSettingsAsync(string archived, CancellationToken cancellationToken)
    {
        var archivedFile = Path.Combine(archived, "cockpit.json");
        if (!File.Exists(archivedFile))
        {
            return null;
        }

        return JsonNode.Parse(await File.ReadAllTextAsync(archivedFile, cancellationToken)) as JsonObject
            ?? throw new InvalidOperationException("The cockpit.json in this backup could not be read, so nothing was restored.");
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

    // An archive carries no plugin binaries since AC-1276, so a restore fetches them from their stores again before
    // the settings that belong to them are written (AC-1279). `Pinned` is the checksum each actually came back on,
    // not always the archive's; `Notes` is the one sentence per plugin the operator has to be told.
    private async Task<(Dictionary<string, string> Pinned, IReadOnlyList<RestorePluginNote> Notes, bool Stopped)> _FetchPluginsAsync(
        string root,
        JsonObject? incoming,
        BackupManifest manifest,
        RestoreOptions options,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<RestorePluginNote>();
        var wanted = new List<string>();

        foreach (var id in options.Plugins)
        {
            var (skip, note) = _AlreadyOnDisk(root, id, manifest.Plugins);

            if (note is not null)
            {
                logger.LogInformation("Restoring plugin '{Plugin}': {Note}", id, note);
                notes.Add(new RestorePluginNote(id, note));
            }

            if (!skip)
            {
                wanted.Add(id);
            }
        }

        if (wanted.Count == 0)
        {
            return (pinned, notes, false);
        }

        var (catalogue, unreadable) = await _CataloguesAsync(incoming, cancellationToken);
        var requests = new List<PluginProvisionRequest>();

        // Held rather than added: a plugin coming back on another version is worth saying, but if its install then
        // fails, the failure is the one thing to say about it. One note per plugin, never two.
        var ifItLands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in wanted)
        {
            var (request, report, note) = _Pick(catalogue, unreadable, id, manifest.Plugins.GetValueOrDefault(id));
            logger.LogInformation("Restoring plugin '{Plugin}': {Report}", id, report);

            if (request is null)
            {
                notes.Add(new RestorePluginNote(id, note ?? report));
                continue;
            }

            if (note is not null)
            {
                ifItLands[request.Id] = note;
            }

            requests.Add(request);
        }

        // One plugin per call rather than one batch call for all of them: the batch cannot be stopped between
        // plugins, and a fetch of eleven that will not stop is the whole complaint (AC-1281). The isolation is
        // still the batch's own — a store that dies on the third plugin does not take the fourth with it.
        for (var index = 0; index < requests.Count; index++)
        {
            // Between plugins, never inside one: what a stop leaves behind is whole plugins, and nothing is rolled
            // back — an installed plugin without a registration asks for consent at the next start, which is milder
            // than deleting it. Everything not reached is named, so a stop leaves no mystery.
            if (cancellationToken.IsCancellationRequested)
            {
                notes.AddRange(requests.Skip(index).Select(request =>
                    new RestorePluginNote(request.Id, "the restore was stopped before it was fetched.")));

                return (pinned, notes, true);
            }

            progress?.Report(new RestoreProgress(RestoreStage.FetchingPlugins, index, requests.Count));

            var request = requests[index];
            var result = (await provisioning.InstallManyAsync([request], AbstractionsContract.Version)).Results[0];

            if (result is { IsSuccess: true, FolderId: { } folderId, Sha256: { } sha256 })
            {
                pinned[folderId] = sha256;

                if (ifItLands.TryGetValue(result.Id, out var landed))
                {
                    notes.Add(new RestorePluginNote(result.Id, landed));
                }
            }
            else
            {
                // The reason the attempt itself produced, not one inferred from a folder that is not there: the
                // operator is told a checksum was rejected or this cockpit is too old, rather than "not installed".
                var reason = result.Outcome == PluginProvisionOutcome.Incompatible
                    ? $"this cockpit cannot run it: {result.Error}"
                    : result.Error ?? "the store could not hand it over.";

                logger.LogWarning("Plugin '{Plugin}' did not come back ({Outcome}): {Reason}", result.Id, result.Outcome, reason);
                notes.Add(new RestorePluginNote(result.Id, reason));
            }
        }

        progress?.Report(new RestoreProgress(RestoreStage.FetchingPlugins, requests.Count, requests.Count));

        return (pinned, notes, false);
    }

    // Whether what is on disk makes fetching pointless, and what to tell the operator when it does.
    // `PluginSourceInstaller` refuses to roll an installed plugin back to an older build and a restore must not be
    // the way around that rule — so a locally newer one stays, and is said out loud instead of passed over.
    private static (bool Skip, string? Note) _AlreadyOnDisk(string root, string id, IReadOnlyDictionary<string, string> archivedVersions)
    {
        var folder = Path.Combine(root, "plugins", id);
        if (!Directory.Exists(folder))
        {
            return (false, null);
        }

        if (archivedVersions.GetValueOrDefault(id) is not { Length: > 0 } archived || archived == UnknownVersion)
        {
            return (true, null);
        }

        var installed = _VersionOf(folder);
        if (PluginVersion.IsNewer(archived, installed))
        {
            return (false, null);
        }

        return (true, PluginVersion.IsNewer(installed, archived)
            ? $"{installed} is already installed here and is newer than the {archived} in the backup, so it was left alone rather than rolled back."
            : null);
    }

    // The stores to look in: the ones configured here first — they are the ones that still carry their token, which
    // the archive scrubs out — then any the archive named that this cockpit does not have, which on a machine set up
    // from scratch is all of them.
    private async Task<(List<(PluginStoreConfig Store, PluginStoreIndex Index)> Catalogue, List<PluginStoreConfig> Unreadable)> _CataloguesAsync(
        JsonObject? incoming,
        CancellationToken cancellationToken)
    {
        var configured = (await stores.LoadAsync(cancellationToken)).ToList();

        foreach (var archived in _ArchivedStores(incoming).Where(store => !configured.Any(store.SameStoreAs)))
        {
            configured.Add(archived);
        }

        var catalogue = new List<(PluginStoreConfig, PluginStoreIndex)>();
        var unreadable = new List<PluginStoreConfig>();

        foreach (var store in configured)
        {
            // Free to stop here: reading an index writes nothing, so nothing has happened yet to leave half-done.
            cancellationToken.ThrowIfCancellationRequested();

            var fetched = await storeClient.FetchIndexAsync(store, cancellationToken);

            if (fetched is { IsSuccess: true, Index: { } index })
            {
                catalogue.Add((store, index));
            }
            else
            {
                logger.LogWarning("Store {Store} could not be read while restoring: {Error}", store.Location, fetched.Error);
                unreadable.Add(store);
            }
        }

        return (catalogue, unreadable);
    }

    // Named, not counted: a local store is a path the operator picked themselves, and "D:\plugin-store is not here"
    // is something they can act on where "a store failed" is not.
    private static string _Unreadable(IReadOnlyList<PluginStoreConfig> stores) =>
        string.Join(", ", stores.Select(store => store.IsLocal ? $"the local store '{store.Location}'" : $"the store {store.Location}"));

    private static IEnumerable<PluginStoreConfig> _ArchivedStores(JsonObject? incoming)
    {
        try
        {
            return incoming?["PluginStores"].Deserialize<List<PluginStoreConfig>>()?.Where(store => store is not null) ?? [];
        }
        catch (JsonException)
        {
            // A hand-edited store list is not worth failing a restore over; the configured stores still stand.
            return [];
        }
    }

    // Which store version a plugin comes back as — the four ways that can land (AC-1279), each of which says what it
    // did rather than passing over it. The exact archived version goes to the provisioner as it is: whether this host
    // can run it is its call, not a second opinion here.
    private static (PluginProvisionRequest? Request, string Report, string? Note) _Pick(
        IReadOnlyList<(PluginStoreConfig Store, PluginStoreIndex Index)> catalogue,
        IReadOnlyList<PluginStoreConfig> unreadable,
        string id,
        string? archivedVersion)
    {
        var found = catalogue
            .Select(source => (source.Store, Entry: source.Index.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, id, StringComparison.OrdinalIgnoreCase))))
            .Where(candidate => candidate.Entry is not null)
            .ToList();

        if (found.Count == 0)
        {
            // Two different truths that used to read as one (Raymond, AC-1279). "Nobody publishes it" is the end of
            // the road; "the store it came from is a path that is not on this machine" is something the operator can
            // put right, and saying so beats silently moving a path they chose themselves.
            var gone = unreadable.Count == 0
                ? "none of the stores carries it any more, so its settings are back and its binaries are not."
                : $"none of the stores that could be read carries it, and {_Unreadable(unreadable)} could not be read — "
                  + "set that store up again on this machine and restore once more.";

            return (null, gone, gone);
        }

        // The rule when two stores publish the same id, a decision and not a detail: the first configured store
        // carrying it wins. `PinnedSha256` cannot settle it — that hashes the installed folder, a store's `Sha256`
        // hashes the zip, so the two never compare equal. Order the operator can see beats a comparison that cannot.
        var (store, entry) = found[0];
        var wanted = archivedVersion is { Length: > 0 } and not UnknownVersion ? archivedVersion : null;

        if (entry!.Versions.FirstOrDefault(version => string.Equals(version.Version, wanted, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            // The only outcome with nothing to tell the operator: they get back exactly what they backed up.
            return (new PluginProvisionRequest(id, entry.Name, store, exact), $"fetching {wanted}, the version it was backed up at.", null);
        }

        var missed = wanted is null
            ? "the archive does not record which version it was at"
            : $"the store no longer offers {wanted}";

        var newest = entry.Versions
            .Where(version => PluginCompatibility.IsCompatible(version, AbstractionsContract.Version, HostVersionInfo.Current))
            .Aggregate((PluginStoreVersion?)null, (best, version) => best is null || PluginVersion.IsNewer(version.Version, best.Version) ? version : best);

        if (newest is null)
        {
            var unrunnable = $"{missed}, and no version it does offer runs on this cockpit.";

            return (null, unrunnable, unrunnable);
        }

        // Said out loud on purpose: skipping it silently loses someone a plugin, and upgrading it silently changes
        // what they run. Neither is a thing a restore may decide on its own without saying so.
        var moved = $"{missed}, so it is put back on {newest.Version} instead.";

        return (new PluginProvisionRequest(id, entry.Name, store, newest), moved, moved);
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

    // The plugins the archive asks a restore to fetch back, read off the folders — id and version, no store. Nothing
    // on disk records which store a plugin came from, and asking every store here would let an expired token drop one
    // in silence. `_Pick` resolves the store at restore, where the indexes are fresh and a failure can be reported.
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
                : UnknownVersion;
        }
        catch (JsonException)
        {
            // A plugin whose manifest we cannot read is still listed; only its version line is a shrug.
            return UnknownVersion;
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
