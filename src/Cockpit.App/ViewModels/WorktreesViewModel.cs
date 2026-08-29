using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;

namespace Cockpit.App.ViewModels;

// AC-1013: The cockpit's view on the git worktrees it created (AC-85): which ones still exist, whether...
public sealed partial class WorktreesViewModel : ObservableObject, ISingletonService
{
    private readonly IWorktreeManager? _manager;
    private readonly ISessionDialogService? _dialogs;

    // Design-time/previewer: a couple of rows so the dialog renders without a live manager behind it.
    public WorktreesViewModel()
    {
        var record = new WorktreeRecord("gone-session", "/home/me/project", "/state/worktrees/ab12/cockpit-fix-1", "cockpit/fix-1", "0123456789abcdef0123456789abcdef01234567", DateTimeOffset.Now.AddHours(-2)) { IsRetained = true };
        Worktrees.Add(new ManagedWorktreeRowViewModel(new WorktreeStatus(record, Exists: true, HasUncommittedChanges: true, StrandableCommits: 0), isOwnerLive: false));
        Count = Worktrees.Count;
    }

    public WorktreesViewModel(IWorktreeManager manager, ISessionDialogService dialogs)
    {
        _manager = manager;
        _dialogs = dialogs;
    }

    public ObservableCollection<ManagedWorktreeRowViewModel> Worktrees { get; } = [];

    // How many worktrees the cockpit manages right now — the status-bar counter.
    [ObservableProperty]
    private int _count;

    public bool HasWorktrees => Count > 0;

    // Quiet grey when there are none, the working colour when there are: knowing some are left behind is worth seeing at a glance.
    public string CountBrushKey => Count > 0 ? "CockpitStatusBusyBrush" : "CockpitTextFaintBrush";

    // Why the last removal did not go through, in git's own words — null while nothing has failed. Shown in the
    // dialog because a row that stays put is otherwise indistinguishable from a button that does nothing (AC-342).
    [ObservableProperty]
    private string? _removeFailure;

    public bool HasRemoveFailure => RemoveFailure is not null;

    // What a successful removal left behind (AC-507) — for example a worktree folder abandoned on disk because its
    // repository was gone. Distinct from `RemoveFailure`: the removal went through, this is information
    // about it, not a reason it failed. Null when the last removal had nothing to mention.
    [ObservableProperty]
    private string? _removeNotice;

    public bool HasRemoveNotice => RemoveNotice is not null;

    // Supplied by the cockpit: the ids of the sessions alive right now, so each worktree's owner shows as live or gone.
    public Func<IReadOnlySet<string>>? LiveSessionIds { get; set; }

    // AC-1013: Supplied by the cockpit (AC-520): the display names of the sessions, by pane id — the live...
    public Func<IReadOnlyDictionary<string, string>>? SessionNames { get; set; }

    // AC-1013: Supplied by the cockpit (AC-520 fix 6): the pane ids that currently show an open restore...
    public Func<IReadOnlySet<string>>? RestoreOfferPaneIds { get; set; }

    // Raised when the operator reattaches to a gone worktree; the cockpit starts a new session in it.
    public event Action<WorktreeRecord>? ReattachRequested;

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasWorktrees));
        OnPropertyChanged(nameof(CountBrushKey));
    }

    partial void OnRemoveFailureChanged(string? value) => OnPropertyChanged(nameof(HasRemoveFailure));

    partial void OnRemoveNoticeChanged(string? value) => OnPropertyChanged(nameof(HasRemoveNotice));

    // The cheap refresh for the status-bar counter: how many worktrees exist, without asking git about each one's state.
    public async Task RefreshCountAsync()
    {
        if (_manager is null)
        {
            return;
        }

        Count = (await _manager.ListAsync()).Count;
    }

    // The full refresh for the dialog: each worktree's git state and whether its owner is still alive.
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_manager is null)
        {
            return;
        }

        var live = LiveSessionIds?.Invoke() ?? new HashSet<string>();
        var names = SessionNames?.Invoke() ?? ReadOnlyDictionary<string, string>.Empty;
        var restoreOfferOwners = RestoreOfferPaneIds?.Invoke() ?? new HashSet<string>();
        var statuses = await _manager.GetStatusesAsync();

        Worktrees.Clear();
        foreach (var status in statuses)
        {
            var owner = status.Record.SessionId;
            Worktrees.Add(new ManagedWorktreeRowViewModel(status, live.Contains(owner), names.GetValueOrDefault(owner), restoreOfferOwners.Contains(owner)));
        }

        Count = Worktrees.Count;
    }

    // AC-1013: Removes a worktree, always after a confirmation. A tree with uncommitted changes gets the...
    [RelayCommand]
    private async Task RemoveAsync(ManagedWorktreeRowViewModel? row)
    {
        if (_manager is null || row is null || row.IsOwnerLive)
        {
            return;
        }

        var (title, message, confirmLabel) = row.Status.HasUncommittedChanges
            ? ("Delete worktree with unsaved changes?",
                $"The worktree on branch '{row.Branch}' has uncommitted changes that will be lost. Its committed history stays on the branch.",
                "Delete anyway")
            : ("Remove worktree?",
                $"Remove the worktree on branch '{row.Branch}'? The branch itself is kept.",
                "Remove");

        if (_dialogs is null || !await _dialogs.ShowConfirmationDialogAsync(title, message, confirmLabel))
        {
            return;
        }

        try
        {
            RemoveNotice = await _manager.RemoveAsync(row.Record, force: row.Status.HasUncommittedChanges) is { } notice
                ? _OneLine(notice)
                : null;
            RemoveFailure = null;
        }
        catch (Exception exception)
        {
            // A remove git declines (a lock we could not clear, a folder in use) leaves the row where it is — and
            // says so, rather than leaving the operator with a button that appears to do nothing. The refresh below
            // still shows the row's real current state rather than pretending it went.
            RemoveFailure = _OneLine($"Could not remove '{row.Branch}' — {exception.Message}");
            RemoveNotice = null;
        }

        await RefreshAsync();
    }

    // Hands a gone worktree back to a fresh session (reattach); blocked for a live one.
    [RelayCommand]
    private void Reattach(ManagedWorktreeRowViewModel? row)
    {
        if (row is null || !row.CanReattach)
        {
            return;
        }

        ReattachRequested?.Invoke(row.Record);
    }

    // AC-1013: Gives up a worktree's claim on a session that is only "live" because of an open restore...
    [RelayCommand]
    private async Task ReleaseAsync(ManagedWorktreeRowViewModel? row)
    {
        if (_manager is null || row is null || !row.CanRelease)
        {
            return;
        }

        var confirmed = _dialogs is not null && await _dialogs.ShowConfirmationDialogAsync(
            "Release this worktree?",
            $"This detaches the worktree on branch '{row.Branch}' from the session that claimed it. That session's " +
            "restore offer will no longer have a worktree to come back to, so it is discarded along with this. " +
            "No files are touched — afterwards you can remove the worktree or start a new session in it.",
            "Release");
        if (!confirmed)
        {
            return;
        }

        await _manager.ReleaseOwnershipAsync(row.Record.Path);
        await RefreshAsync();
    }

    // Removes every worktree that is safe to remove — clean or already gone, no work to lose. Never touches one with unsaved changes.
    [RelayCommand]
    private async Task CleanUpFinishedAsync()
    {
        if (_manager is null)
        {
            return;
        }

        // AC-1013: Only clean trees whose session is gone: a live session's tree is never pulled from under...
        List<string> refusals = [];
        List<string> notices = [];
        foreach (var row in Worktrees.Where(worktree => (worktree.IsClean || worktree.Status.NothingToKeep) && !worktree.IsOwnerLive).ToList())
        {
            try
            {
                if (await _manager.RemoveAsync(row.Record, force: false) is { } notice)
                {
                    notices.Add(notice);
                }
            }
            catch (Exception exception)
            {
                // Skip one that will not remove; the rest still get cleaned. What was skipped is collected rather
                // than swallowed — a sweep that silently leaves rows behind reads as a sweep that did nothing.
                refusals.Add($"'{row.Branch}' — {exception.Message}");
            }
        }

        RemoveFailure = refusals.Count > 0
            ? _OneLine($"Could not remove {refusals.Count} of them: {string.Join("; ", refusals)}")
            : null;
        RemoveNotice = notices.Count > 0 ? _OneLine(string.Join(" ", notices)) : null;

        await RefreshAsync();
    }

    // AC-1013: git says why across several lines; the dialog shows it on one. Beyond reading better in a...
    private static string _OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
