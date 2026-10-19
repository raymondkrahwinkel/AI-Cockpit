using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Backup;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Backup;

// Persists the scheduled-backup settings and state under the `backup` section of `cockpit.json`, read-modify-write
// via `CockpitConfigFileAccess` so other sections stay untouched.
internal sealed class BackupScheduleStore : IBackupScheduleStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public BackupScheduleStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal BackupScheduleStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<BackupSchedule> LoadAsync(CancellationToken cancellationToken = default) =>
        (await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.Backup?.ToSchedule() ?? new BackupSchedule();

    public Task SaveAsync(BackupSchedule schedule, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file => (file.Backup ??= new BackupScheduleEntry()).Apply(schedule), cancellationToken);

    public async Task<BackupScheduleState> LoadStateAsync(CancellationToken cancellationToken = default) =>
        (await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.Backup?.ToState() ?? new BackupScheduleState();

    public Task SaveStateAsync(BackupScheduleState state, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file => (file.Backup ??= new BackupScheduleEntry()).Apply(state), cancellationToken);
}
