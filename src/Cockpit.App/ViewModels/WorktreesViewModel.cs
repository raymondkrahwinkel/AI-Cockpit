using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The cockpit's view on the git worktrees it created (AC-85): which ones still exist, whether each is clean or
/// holds work, and whether the session that owns it is still alive — so a crash-orphaned worktree can be reattached
/// to a fresh session or removed, and no removal ever loses work without asking. Shared as a singleton so the
/// status-bar counter and the dialog read the same list.
/// </summary>
public sealed partial class WorktreesViewModel : ObservableObject, ISingletonService
{
    private readonly IWorktreeManager? _manager;
    private readonly ISessionDialogService? _dialogs;

    // Design-time/previewer: a couple of rows so the dialog renders without a live manager behind it.
    public WorktreesViewModel()
    {
        var record = new WorktreeRecord("gone-session", "/home/me/project", "/state/worktrees/ab12/cockpit-fix-1", "cockpit/fix-1", "0123456789abcdef0123456789abcdef01234567", DateTimeOffset.Now.AddHours(-2)) { IsRetained = true };
        Worktrees.Add(new ManagedWorktreeRowViewModel(new WorktreeStatus(record, Exists: true, HasUncommittedChanges: true, CommitsAhead: 0), isOwnerLive: false));
        Count = Worktrees.Count;
    }

    public WorktreesViewModel(IWorktreeManager manager, ISessionDialogService dialogs)
    {
        _manager = manager;
        _dialogs = dialogs;
    }

    public ObservableCollection<ManagedWorktreeRowViewModel> Worktrees { get; } = [];

    /// <summary>How many worktrees the cockpit manages right now — the status-bar counter.</summary>
    [ObservableProperty]
    private int _count;

    public bool HasWorktrees => Count > 0;

    /// <summary>Quiet grey when there are none, the working colour when there are: knowing some are left behind is worth seeing at a glance.</summary>
    public string CountBrushKey => Count > 0 ? "CockpitStatusBusyBrush" : "CockpitTextFaintBrush";

    /// <summary>Supplied by the cockpit: the ids of the sessions alive right now, so each worktree's owner shows as live or gone.</summary>
    public Func<IReadOnlySet<string>>? LiveSessionIds { get; set; }

    /// <summary>Raised when the operator reattaches to a gone worktree; the cockpit starts a new session in it.</summary>
    public event Action<WorktreeRecord>? ReattachRequested;

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasWorktrees));
        OnPropertyChanged(nameof(CountBrushKey));
    }

    /// <summary>The cheap refresh for the status-bar counter: how many worktrees exist, without asking git about each one's state.</summary>
    public async Task RefreshCountAsync()
    {
        if (_manager is null)
        {
            return;
        }

        Count = (await _manager.ListAsync()).Count;
    }

    /// <summary>The full refresh for the dialog: each worktree's git state and whether its owner is still alive.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_manager is null)
        {
            return;
        }

        var live = LiveSessionIds?.Invoke() ?? new HashSet<string>();
        var statuses = await _manager.GetStatusesAsync();

        Worktrees.Clear();
        foreach (var status in statuses)
        {
            Worktrees.Add(new ManagedWorktreeRowViewModel(status, live.Contains(status.Record.SessionId)));
        }

        Count = Worktrees.Count;
    }

    /// <summary>
    /// Removes a worktree. A tree with uncommitted changes is only removed after an explicit consent that names the
    /// loss (its committed history stays on the branch; only unsaved edits go). A clean or commits-only tree removes
    /// straight away — git itself allows that, and the branch is kept.
    /// </summary>
    [RelayCommand]
    private async Task RemoveAsync(ManagedWorktreeRowViewModel? row)
    {
        if (_manager is null || row is null)
        {
            return;
        }

        if (row.Status.HasUncommittedChanges)
        {
            var confirmed = _dialogs is not null && await _dialogs.ShowConfirmationDialogAsync(
                "Delete worktree with unsaved changes?",
                $"The worktree on branch '{row.Branch}' has uncommitted changes that will be lost. Its committed history stays on the branch.",
                "Delete anyway");
            if (!confirmed)
            {
                return;
            }
        }

        try
        {
            await _manager.RemoveAsync(row.Record, force: row.Status.HasUncommittedChanges);
        }
        catch (Exception)
        {
            // A remove git declines (a lock we could not clear, a folder in use) leaves the row where it is; the
            // refresh below shows its real current state rather than pretending it went.
        }

        await RefreshAsync();
    }

    /// <summary>Hands a gone worktree back to a fresh session (reattach); blocked for a live one.</summary>
    [RelayCommand]
    private void Reattach(ManagedWorktreeRowViewModel? row)
    {
        if (row is null || !row.CanReattach)
        {
            return;
        }

        ReattachRequested?.Invoke(row.Record);
    }

    /// <summary>Removes every worktree that is safe to remove — clean, no work to lose. Never touches one with unsaved changes.</summary>
    [RelayCommand]
    private async Task CleanUpFinishedAsync()
    {
        if (_manager is null)
        {
            return;
        }

        foreach (var row in Worktrees.Where(worktree => worktree.IsClean).ToList())
        {
            try
            {
                await _manager.RemoveAsync(row.Record, force: false);
            }
            catch (Exception)
            {
                // Skip one that will not remove; the rest still get cleaned, and the refresh shows what remains.
            }
        }

        await RefreshAsync();
    }
}
