namespace Cockpit.Core.Backup;

// AC-1502: when the cockpit backs itself up, how many it keeps, and where to. A folder is a path of this machine and
// never travels with a manifest. `Keep` and `IntervalHours` are floored at 1: a Keep of 0 would be "delete all".
public sealed record BackupSchedule(
    bool Enabled = false,
    int IntervalHours = BackupSchedule.DailyHours,
    int Keep = 7,
    string TargetId = BackupSchedule.LocalFolderTargetId,
    string? Folder = null)
{
    public const int DailyHours = 24;

    public const int WeeklyHours = 168;

    public const string LocalFolderTargetId = "local-folder";

    public TimeSpan Interval => TimeSpan.FromHours(Math.Max(1, IntervalHours));

    public int KeepAtLeastOne => Math.Max(1, Keep);
}

// What the last scheduled runs left behind: when one last succeeded (the next one is due an interval after), and why
// the last one failed, until a later one succeeds. Kept on disk so a failure outlives the session that saw it.
public sealed record BackupScheduleState(DateTimeOffset? LastSuccess = null, string? LastFailure = null);
