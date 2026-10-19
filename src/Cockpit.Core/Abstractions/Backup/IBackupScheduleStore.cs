using Cockpit.Core.Backup;

namespace Cockpit.Core.Abstractions.Backup;

/// <summary>
/// Loads and persists the scheduled-backup settings and what the last runs left behind, in the <c>backup</c> section
/// of <c>cockpit.json</c> (AC-1502). Nothing saved means a schedule that is off.
/// </summary>
public interface IBackupScheduleStore
{
    Task<BackupSchedule> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(BackupSchedule schedule, CancellationToken cancellationToken = default);

    Task<BackupScheduleState> LoadStateAsync(CancellationToken cancellationToken = default);

    Task SaveStateAsync(BackupScheduleState state, CancellationToken cancellationToken = default);
}
