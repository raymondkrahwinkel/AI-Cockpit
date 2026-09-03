using Cockpit.Core.Backup;

namespace Cockpit.Core.Abstractions.Backup;

/// <summary>
/// Backing the cockpit up and putting it back (#70) — the settings, the profiles, the plugins and everything they
/// stored. The same archive is what you carry to another machine, which is the use it will actually get.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Writes an archive to <paramref name="archivePath"/> and returns what it says about itself — including the secrets it stripped, which the operator will have to type in again.
    /// </summary>
    Task<BackupManifest> WriteAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a backup's manifest without restoring it, so the operator can be shown what they are about to overwrite themselves with.
    /// </summary>
    Task<BackupManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts back what <paramref name="options"/> asks for, and nothing else: the cockpit's own settings, and the
    /// registrations of whichever plugins the operator chose — their binaries are fetched from their store again,
    /// not carried by the archive. What it replaces is set aside rather than deleted — a restore is the one act
    /// here that can cost someone a day. <paramref name="progress"/> reports which stage it is in, and so whether
    /// stopping is still on offer; the return says whether it was stopped and which plugins are still not here.
    /// </summary>
    Task<RestoreReport> RestoreAsync(
        string archivePath,
        RestoreOptions options,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
