using System.Collections.ObjectModel;
using System.Globalization;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Backup;
using Cockpit.Plugins.Abstractions.Backup;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

// AC-1502: Options → Backup → the scheduled backup. Written straight through, like the backup buttons beside it: the
// schedule is a backend setting the planner reads, and a Cancel that put it back would leave it running meanwhile.
public sealed partial class BackupScheduleViewModel(IBackupScheduleStore store, ILogger? logger = null) : ViewModelBase
{
    public const string Daily = "Daily";

    public const string Weekly = "Weekly";

    private BackupSchedule _loaded = new();
    private BackupScheduleState _state = new();
    private bool _loading;

    public IReadOnlyList<string> Intervals { get; } = [Daily, Weekly];

    private static readonly BackupDestination LocalFolder = new("Local folder", BackupSchedule.LocalFolderTargetId);

    // The local folder plus what plugins registered (AC-1508), refreshed when Options opens.
    public ObservableCollection<BackupDestination> Targets { get; } = [LocalFolder];

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _interval = Daily;

    [ObservableProperty]
    private int _keep = 7;

    [ObservableProperty]
    private BackupDestination? _target = LocalFolder;

    // What the last scheduled run left behind: the failure that is still standing, else when and where the last one went.
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
            _state = await store.LoadStateAsync();

            Enabled = _loaded.Enabled;
            Interval = _loaded.IntervalHours >= BackupSchedule.WeeklyHours ? Weekly : Daily;
            Keep = _loaded.KeepAtLeastOne;
            Target = _TargetFor(_loaded.TargetId);
            _ShowStatus();
        }
        finally
        {
            _loading = false;
        }
    }

    // Options opens: a run since the start is read now, not only after a restart. The schedule fields stay as they are.
    public async Task RefreshStatusAsync()
    {
        try
        {
            _state = await store.LoadStateAsync();
            _ShowStatus();
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Reading the scheduled backup status failed.");
        }
    }

    private void _ShowStatus() =>
        Status = _state switch
        {
            { LastFailure: { } failure } => failure,
            { LastSuccess: { } done } => $"Last backup: {done.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} to {Target?.Name}.",
            _ => string.Empty,
        };

    partial void OnEnabledChanged(bool value) => _Save();

    partial void OnIntervalChanged(string value) => _Save();

    partial void OnKeepChanged(int value) => _Save();

    partial void OnTargetChanged(BackupDestination? value) => _Save();

    public void RefreshTargets(IEnumerable<IBackupTarget> registered)
    {
        _loading = true;
        try
        {
            Targets.Clear();
            Targets.Add(LocalFolder);
            foreach (var target in registered)
            {
                Targets.Add(new BackupDestination(target.DisplayName, target.Id, target));
            }

            Target = _TargetFor(_loaded.TargetId);
            _ShowStatus();
        }
        finally
        {
            _loading = false;
        }
    }

    // A saved target whose plugin is not here stays chosen under its id, rather than quietly becoming the local folder.
    private BackupDestination _TargetFor(string id)
    {
        if (Targets.FirstOrDefault(target => target.Id == id) is { } known)
        {
            return known;
        }

        var missing = new BackupDestination($"{id} (not available)", id);
        Targets.Add(missing);
        return missing;
    }

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
            TargetId = Target?.Id ?? BackupSchedule.LocalFolderTargetId,
        };

        Saving = _SaveAsync(_loaded);
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
            logger?.LogWarning(exception, "Saving the backup schedule failed.");
            Status = $"The schedule was not saved: {exception.Message}";
        }
    }
}
