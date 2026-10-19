using System.Text.Json;
using Cockpit.Core.Backup;
using Cockpit.Plugins.Abstractions.Backup;

namespace Cockpit.Infrastructure.Backup;

// The first backup target, and the only one a cockpit without plugins has (AC-1501): a folder on this machine, one
// zip per backup. Copied into `staging` and moved into place from there, so the archive lands as it did before the
// target existed — whole or not at all, a busy file waited out by the same move, and no `.partial` in the folder.
internal sealed class LocalFolderBackupTarget(string folder, string staging) : IBackupTarget
{
    public string Id => "local-folder";

    public string DisplayName => folder;

    public async Task<string> WriteAsync(string name, Stream archive, CancellationToken cancellationToken = default)
    {
        var partial = Path.Combine(staging, $"cockpit-backup-{Guid.NewGuid():n}.partial");

        try
        {
            await using (var file = File.Create(partial))
            {
                await archive.CopyToAsync(file, cancellationToken);
            }

            await BackupService.MoveIntoPlaceAsync(partial, _PathOf(name), cancellationToken);

            return name;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    public async Task<IReadOnlyList<BackupTargetEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<BackupTargetEntry>();

        foreach (var path in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.zip") : [])
        {
            if (await _EntryForAsync(path, cancellationToken) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public Task<Stream> OpenReadAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(File.OpenRead(_PathOf(id)));

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        // Already gone is as good as deleted: retention wants the backup absent, not the credit for removing it.
        File.Delete(_PathOf(id));

        return Task.FromResult(true);
    }

    // An id is a file name in this folder and nothing else: a `..\` handed in from outside must not reach past it.
    private string _PathOf(string name) =>
        name.Length > 0 && Path.GetFileName(name) == name
            ? Path.Combine(folder, name)
            : throw new ArgumentException($"'{name}' is not a backup in {folder}.", nameof(name));

    // A zip in the folder that is not a cockpit backup is somebody else's file, not a broken backup: left out.
    private static async Task<BackupTargetEntry?> _EntryForAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var manifest = await BackupManifest.ReadAsync(stream, cancellationToken);

            return new BackupTargetEntry(Path.GetFileName(path), manifest.CreatedUtc, stream.Length, manifest.AppVersion);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }
}
