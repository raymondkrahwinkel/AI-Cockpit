using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
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

    private static string CockpitDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cockpit");

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

                    // The settings are the one file that may be rewritten on the way in.
                    if (!options.IncludeCredentials && string.Equals(relative, "cockpit.json", StringComparison.OrdinalIgnoreCase))
                    {
                        removed.AddRange(await _WriteScrubbedSettingsAsync(archive, entryName, file, cancellationToken));
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
                    profileDirectories);

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

    public async Task RestoreAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(archivePath, cancellationToken);

        if (!manifest.CanRestore)
        {
            throw new InvalidOperationException(
                $"This backup was made by a newer cockpit (layout {manifest.Schema}, this one reads {BackupManifest.CurrentSchema}). Update first — a partial restore of a layout we do not know is worse than none.");
        }

        // Unpack first, swap second. Everything that can fail — a corrupt entry, a full disk, a locked file — fails
        // while the real cockpit directory is still untouched.
        var staging = Path.Combine(Path.GetTempPath(), $"cockpit-restore-{Guid.NewGuid():n}");

        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging);

            var restored = Path.Combine(staging, "cockpit");
            if (!Directory.Exists(restored))
            {
                throw new InvalidOperationException("This backup carries no cockpit directory, so there is nothing to restore.");
            }

            var root = CockpitDirectory;
            var aside = $"{root}.replaced-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";

            // The models are not in the archive, and they are worth keeping: they are the one thing here that costs
            // gigabytes to fetch again.
            if (Directory.Exists(root))
            {
                Directory.Move(root, aside);

                foreach (var kept in BackupContents.Excluded)
                {
                    var source = Path.Combine(aside, kept);
                    if (Directory.Exists(source))
                    {
                        Directory.Move(source, Path.Combine(restored, kept));
                    }
                }
            }

            Directory.Move(restored, root);

            // The profiles' own config directories, when the backup carries them. Each is put back where the manifest
            // says it lived, and whatever is there now is moved aside rather than merged: two half-merged agent logins
            // is a state nobody could reason about afterwards.
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

            logger.LogInformation("Restored the cockpit from {Path}; the previous directory is at {Aside}", archivePath, aside);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> _WriteScrubbedSettingsAsync(
        ZipArchive archive,
        string entryName,
        string file,
        CancellationToken cancellationToken)
    {
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken))
            ?? throw new InvalidOperationException("cockpit.json could not be read, so the backup would not have been one.");

        var removed = SecretScrubber.Scrub(settings);

        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(settings.ToJsonString(Json)), cancellationToken);

        return removed;
    }

    private async Task<Dictionary<string, string>> _WriteProfileConfigsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var profile in await profiles.LoadAsync(cancellationToken))
        {
            if (!Directory.Exists(profile.ConfigDir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(profile.ConfigDir, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(profile.ConfigDir, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, $"profiles/{profile.Label}/{relative}", CompressionLevel.Optimal);
            }

            written[profile.Label] = profile.ConfigDir;
        }

        return written;
    }
}
