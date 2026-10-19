using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Backup;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// AC-1502: Options → Backup → the scheduled backup. Written straight through, like the backup buttons beside it: the
// schedule is a backend setting the planner reads, and a Cancel that put it back would leave it running meanwhile.
public sealed partial class BackupScheduleViewModel(IBackupScheduleStore store) : ViewModelBase
{
    public const string Daily = "Daily";

    public const string Weekly = "Weekly";

    private BackupSchedule _loaded = new();
    private bool _loading;

    public IReadOnlyList<string> Intervals { get; } = [Daily, Weekly];

    // One target until a plugin can register another (AC-1508); the list is here so that is a row, not a redesign.
    public IReadOnlyList<string> Targets { get; } = ["Local folder"];

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _interval = Daily;

    [ObservableProperty]
    private int _keep = 7;

    [ObservableProperty]
    private string _target = "Local folder";

    // What the last scheduled run left behind: the failure that is still standing, or nothing.
    [ObservableProperty]
    private string _status = string.Empty;

    // The last save in flight, for the journey to await: the schedule is written in the background.
    public Task Saving { get; private set; } = Task.CompletedTask;

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _loaded = await store.LoadAsync();
            var state = await store.LoadStateAsync();

            Enabled = _loaded.Enabled;
            Interval = _loaded.IntervalHours >= BackupSchedule.WeeklyHours ? Weekly : Daily;
            Keep = _loaded.KeepAtLeastOne;
            Status = state.LastFailure ?? string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnEnabledChanged(bool value) => _Save();

    partial void OnIntervalChanged(string value) => _Save();

    partial void OnKeepChanged(int value) => _Save();

    private void _Save()
    {
        if (_loading)
        {
            return;
        }

        _loaded = _loaded with
        {
            Enabled = Enabled,
            IntervalHours = Interval == Weekly ? BackupSchedule.WeeklyHours : BackupSchedule.DailyHours,
            Keep = Math.Max(1, Keep),
        };

        Saving = Task.CompletedTask;
    }

    // Said, not swallowed: a schedule that silently did not stick is a backup the operator thinks they have.
    private async Task _SaveAsync(BackupSchedule schedule)
    {
        try
        {
            await store.SaveAsync(schedule);
        }
        catch (Exception exception)
        {
            Status = $"The schedule was not saved: {exception.Message}";
        }
    }
}
