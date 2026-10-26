using Cockpit.Core.Backup;

namespace Cockpit.Core.Abstractions.Backup;

/// <summary>
/// Backing the cockpit up and putting it back (#70) — the settings, the profiles, the plugins and everything they
/// stored. The same archive is what you carry to another machine, which is the use it will actually get.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Whether a backup or a restore is running right now (AC-1502): a scheduled backup waits instead of starting
    /// beside it.
    /// </summary>
    bool IsBusy => false;

    /// <summary>
    /// Writes an archive to <paramref name="archivePath"/> and returns what it says about itself — including the secrets it stripped, which the operator will have to type in again.
    /// </summary>
    Task<BackupManifest> WriteAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the same archive and hands it, whole and readable, to <paramref name="deliver"/> — a backup target's
    /// write (AC-1501). The archive is staged first, so a target never receives a half-built one.
    /// </summary>
    Task<BackupManifest> WriteAsync(Func<Stream, CancellationToken, Task> deliver, BackupOptions options, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// The same restore, off a stream a backup target opened (AC-1501). It is copied into staging first and then
    /// restored exactly as a file is; a stop while copying comes back as a report, like every other stop.
    /// </summary>
    Task<RestoreReport> RestoreAsync(
        Stream source,
        RestoreOptions options,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
