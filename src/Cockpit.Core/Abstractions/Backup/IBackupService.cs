using Cockpit.Core.Backup;

namespace Cockpit.Core.Abstractions.Backup;

/// <summary>
/// Backing the cockpit up and putting it back (#70) — the settings, the profiles, the plugins and everything they
/// stored. The same archive is what you carry to another machine, which is the use it will actually get.
/// </summary>
public interface IBackupService
{
    /// <summary>Writes an archive to <paramref name="archivePath"/> and returns what it says about itself — including the secrets it stripped, which the operator will have to type in again.</summary>
    Task<BackupManifest> WriteAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken = default);

    /// <summary>Reads a backup's manifest without restoring it, so the operator can be shown what they are about to overwrite themselves with.</summary>
    Task<BackupManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>Puts the cockpit back as the archive has it. Destructive, and all-or-nothing: nothing on disk moves until the whole archive has been read and found sound.</summary>
    Task RestoreAsync(string archivePath, CancellationToken cancellationToken = default);
}
