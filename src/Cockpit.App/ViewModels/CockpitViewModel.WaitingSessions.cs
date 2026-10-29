using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

// AC-1925: the sidebar's "Waiting for you" filter. Its rows are the sessions whose status is NeedsAttention, the same
// flag as the row's warning glyph, across this laptop and every server group.
public partial class CockpitViewModel
{
    private readonly HashSet<INotifyPropertyChanged> _watchedForWaiting = [];

    // Kept current whether or not the filter is on, because the filter's count reads it. Holds SessionPanelViewModel
    // and ServerSessionRowViewModel rows themselves, so the list reuses each row's own view model.
    public ObservableCollection<object> WaitingSessions { get; } = [];

    public int WaitingCount => WaitingSessions.Count;

    // A filter with nothing to show stays out of the sidebar, unless it is on: then it must stay to be switched off.
    public bool ShowsWaitingFilter => WaitingCount > 0 || ShowOnlyWaiting;

    public bool ShowsNothingWaiting => ShowOnlyWaiting && WaitingCount == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWaitingFilter), nameof(ShowsNothingWaiting), nameof(ShowGroupedSessions))]
    private bool _showOnlyWaiting;

    public bool ShowGroupedSessions => !ShowOnlyWaiting;

    partial void OnShowOnlyWaitingChanged(bool value) => _ApplyWaitingBadges();

    // Persisted at once, like the sidebar's width and fold: a direct-manipulation setting that outlives a restart.
    [RelayCommand]
    private async Task ToggleWaitingFilterAsync()
    {
        ShowOnlyWaiting = !ShowOnlyWaiting;
        if (_layoutSettingsStore is null)
        {
            return;
        }

        try
        {
            await _PersistLayoutSettingsAsync();
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Saving the Waiting for you filter failed.");
        }
    }

    private void _WireWaitingSessions()
    {
        Sessions.CollectionChanged += (_, _) => _WatchWaitingRows();
        ServerGroups.CollectionChanged += (_, _) => _WatchWaitingRows();
        _WatchWaitingRows();
    }

    // Follows every row that can wait, and lets go of the rows that left, so a closed session is not held by this handler.
    private void _WatchWaitingRows()
    {
        List<INotifyPropertyChanged> rows = [.. Sessions, .. ServerGroups.SelectMany(group => group.Sessions)];
        foreach (var gone in _watchedForWaiting.Where(row => !rows.Contains(row)).ToList())
        {
            gone.PropertyChanged -= _OnWaitingRowChanged;
            _watchedForWaiting.Remove(gone);
        }

        foreach (var row in rows.Where(_watchedForWaiting.Add))
        {
            row.PropertyChanged += _OnWaitingRowChanged;
        }

        _SyncWaitingSessions();
    }

    private void _OnWaitingRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionPanelViewModel.SessionStatus) or nameof(ServerSessionRowViewModel.Status))
        {
            _SyncWaitingSessions();
        }
    }

    // Only the flat list shows a badge, so a row carries one only while it stands in that list.
    private void _ApplyWaitingBadges()
    {
        foreach (var session in Sessions)
        {
            session.MachineBadge = ShowOnlyWaiting && WaitingSessions.Contains(session) ? "here" : null;
        }

        foreach (var group in ServerGroups)
        {
            foreach (var row in group.Sessions)
            {
                row.MachineBadge = ShowOnlyWaiting && WaitingSessions.Contains(row) ? group.Name : null;
            }
        }
    }

    private void _SyncWaitingSessions()
    {
        List<object> waiting =
        [
            .. Sessions.Where(session => session.SessionStatus == SessionStatus.NeedsAttention),
            .. ServerGroups.SelectMany(group => group.Sessions).Where(row => row.Status == SessionStatus.NeedsAttention),
        ];
        _ReconcileInto(WaitingSessions, waiting);
        _ApplyWaitingBadges();
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(ShowsWaitingFilter));
        OnPropertyChanged(nameof(ShowsNothingWaiting));
    }
}
