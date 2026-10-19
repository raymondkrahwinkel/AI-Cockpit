using Cockpit.Core.Backup;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `BackupSchedule` and its state in the `backup` section of `cockpit.json`.
internal sealed class BackupScheduleEntry
{
    public bool Enabled { get; set; }

    public int IntervalHours { get; set; } = BackupSchedule.DailyHours;

    public int Keep { get; set; } = 7;

    public string TargetId { get; set; } = BackupSchedule.LocalFolderTargetId;

    public string? Folder { get; set; }

    public DateTimeOffset? LastSuccess { get; set; }

    public string? LastFailure { get; set; }

    public BackupSchedule ToSchedule() => new(
        Enabled,
        Math.Max(1, IntervalHours),
        Math.Max(1, Keep),
        string.IsNullOrWhiteSpace(TargetId) ? BackupSchedule.LocalFolderTargetId : TargetId,
        string.IsNullOrWhiteSpace(Folder) ? null : Folder);

    public BackupScheduleState ToState() => new(LastSuccess, LastFailure);

    // Only the half being saved is replaced: the settings and the state are written by different callers.
    public void Apply(BackupSchedule schedule)
    {
        Enabled = schedule.Enabled;
        IntervalHours = schedule.IntervalHours;
        Keep = schedule.Keep;
        TargetId = schedule.TargetId;
        Folder = schedule.Folder;
    }

    public void Apply(BackupScheduleState state)
    {
        LastSuccess = state.LastSuccess;
        LastFailure = state.LastFailure;
    }
}
