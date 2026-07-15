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

/// <summary>
/// Makes and restores a backup of the whole cockpit (#70): the settings, the profiles, the plugins and everything they
/// stored — one zip, one manifest.
/// <para>
/// A restore is destructive and therefore all-or-nothing: the archive is unpacked to a temporary directory and read
/// there, and only when the whole of it is sound does anything on disk move. The cockpit directory is kept aside until
/// the swap has finished, so a restore that dies halfway leaves you with what you had rather than with half of each.
/// </para>
/// </summary>
internal sealed class BackupService(
    ISessionProfileStore profiles,
    ILogger<BackupService> logger) : IBackupService, ISingletonService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string CockpitDirectory => CockpitConfigPath.Root;

    public async Task<BackupManifest> WriteAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken = default)
    {
        var root = CockpitDirectory;
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException("There is nothing to back up: this cockpit has never saved anything.");
        }

        var removed = new List<string>();
        var profileDirectories = new Dictionary<string, string>(StringComparer.Ordinal);

        // Written to a temporary file and moved into place: a half-written archive with the right name is a backup
        // you will trust exactly once.
        var staging = Path.Combine(Path.GetTempPath(), $"cockpit-backup-{Guid.NewGuid():n}.zip");

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

                    // A plugin the operator left out takes its binaries and its settings with it.
                    if (_PluginOf(relative) is { } pluginId && !options.Includes(pluginId))
                    {
                        continue;
                    }

                    // The settings are the one file that is rewritten on the way in: secrets out (unless asked for),
                    // and the plugins that were left out taken with them — their whole point is what they stored.
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
                    _PluginsIn(root, options));

                var entry = archive.CreateEntry(BackupManifest.FileName, CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await JsonSerializer.SerializeAsync(stream, manifest, Json, cancellationToken);
            }

            File.Move(staging, archivePath, overwrite: true);

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

    public async Task<BackupManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var entry = archive.GetEntry(BackupManifest.FileName)
            ?? throw new InvalidOperationException("This zip is not a cockpit backup: it has no backup.json.");

        await using var stream = entry.Open();

        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, Json, cancellationToken)
            ?? throw new InvalidOperationException("This backup's manifest could not be read.");
    }

    public async Task RestoreAsync(string archivePath, RestoreOptions options, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(archivePath, cancellationToken);

        if (!manifest.CanRestore)
        {
            throw new InvalidOperationException(
                $"This backup was made by a newer cockpit (layout {manifest.Schema}, this one reads {BackupManifest.CurrentSchema}). Update first — a partial restore of a layout we do not know is worse than none.");
        }

        if (!options.Settings && options.Plugins.Count == 0)
        {
            throw new InvalidOperationException("Nothing was selected, so nothing was restored.");
        }

        // Unpack first, write second. Everything that can fail — a corrupt entry, a full disk — fails while this
        // cockpit is still untouched.
        var staging = Path.Combine(Path.GetTempPath(), $"cockpit-restore-{Guid.NewGuid():n}");

        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging);

            var archived = Path.Combine(staging, "cockpit");
            if (!Directory.Exists(archived))
            {
                throw new InvalidOperationException("This backup carries no cockpit directory, so there is nothing to restore.");
            }

            var root = CockpitDirectory;
            Directory.CreateDirectory(root);

            // What is being replaced is set aside, never deleted: a restore is the one act here that can cost someone
            // a day, and "it is still there, under this name" is the difference between a mistake and a disaster.
            var aside = Path.Combine(Path.GetDirectoryName(root)!, $"{Path.GetFileName(root)}.replaced-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");

            await _RestoreSettingsAsync(root, archived, aside, options, cancellationToken);
            _RestorePlugins(root, archived, aside, options);

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

    // A chosen plugin's folder replaces the one that is there; the ones nobody chose are not touched.
    private static void _RestorePlugins(string root, string archived, string aside, RestoreOptions options)
    {
        foreach (var id in options.Plugins)
        {
            var source = Path.Combine(archived, "plugins", id);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var target = Path.Combine(root, "plugins", id);

            if (Directory.Exists(target))
            {
                var kept = Path.Combine(aside, "plugins", id);
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                Directory.Move(target, kept);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);
        }
    }

    // The files that belong to the cockpit rather than to any plugin: MCP permissions, the delegation audit log.
    private static void _RestoreLooseFiles(string root, string archived, string aside)
    {
        foreach (var file in Directory.EnumerateFiles(archived))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("cockpit.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(root, name);

            if (File.Exists(target))
            {
                Directory.CreateDirectory(aside);
                File.Copy(target, Path.Combine(aside, name), overwrite: true);
            }

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

        if (settings["Plugins"] is JsonObject plugins)
        {
            foreach (var pluginId in plugins.Select(entry => entry.Key).ToList())
            {
                if (!options.Includes(pluginId))
                {
                    plugins.Remove(pluginId);
                }
            }
        }

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

    // Which plugin a path belongs to — "plugins/youtrack/plugin.json" is YouTrack's — or null for everything else.
    private static string? _PluginOf(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');

        return parts.Length >= 2 && parts[0].Equals("plugins", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    // The plugins that went in, with the version each was at. Read from the folders rather than from the settings:
    // what the archive carries is what is on disk.
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
            // A plugin whose manifest we cannot read still goes in the archive; only its version line is a shrug.
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
