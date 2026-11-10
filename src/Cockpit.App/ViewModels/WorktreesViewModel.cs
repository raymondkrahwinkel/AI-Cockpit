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
    private IRemoteWorktrees? _remote;
    private Func<string, string, string, Task<bool>>? _confirm;
    private bool _isRemote;
    private int _remoteRefreshGeneration;
    private string? _remoteReadFailure;

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

    internal WorktreesViewModel(IRemoteWorktrees? remote, Func<string, string, string, Task<bool>> confirm)
    {
        _remote = remote;
        _confirm = confirm;
        _isRemote = true;
        if (remote is null)
        {
            RemoveFailure = _remoteReadFailure = "Worktrees are unavailable while the server is connecting.";
        }
    }

    internal Task UseRemoteAsync(IRemoteWorktrees remote, Func<string, string, string, Task<bool>> confirm) => UiThreadCall.Uncapped(async () =>
    {
        _remote = remote;
        _confirm = confirm;
        _isRemote = true;
        Worktrees.Clear();
        RemoteProjects.Clear();
        SelectedRemoteProject = null;
        Count = 0;
        OnPropertyChanged(nameof(IsRemote));
        CreateWorktreeCommand.NotifyCanExecuteChanged();
        await RefreshAsync();
    });

    public bool IsRemote => _isRemote;

    public ObservableCollection<RemoteWorktreeProject> RemoteProjects { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateWorktreeCommand))]
    private RemoteWorktreeProject? _selectedRemoteProject;

    [ObservableProperty]
    private string? _newWorktreeLabel;

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
        if (_remote is not null)
        {
            await RefreshAsync();
            return;
        }
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
        if (_remote is not null)
        {
            await UiThreadCall.Uncapped(_RefreshRemoteAsync);
            return;
        }
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

    private async Task _RefreshRemoteAsync()
    {
        var remote = _remote;
        if (remote is null)
        {
            return;
        }
        var generation = ++_remoteRefreshGeneration;
        try
        {
            var rows = await remote.RefreshAsync();
            if (generation != _remoteRefreshGeneration || !ReferenceEquals(remote, _remote))
            {
                return;
            }
            var selected = SelectedRemoteProject?.Id;
            RemoteProjects.Clear();
            foreach (var project in remote.Projects)
            {
                RemoteProjects.Add(new RemoteWorktreeProject(project.Id, project.Name));
            }
            SelectedRemoteProject = RemoteProjects.FirstOrDefault(project => project.Id == selected) ?? RemoteProjects.FirstOrDefault();
            Worktrees.Clear();
            foreach (var row in rows)
            {
                Worktrees.Add(new ManagedWorktreeRowViewModel(row));
            }
            Count = Worktrees.Count;
            if (RemoveFailure == _remoteReadFailure)
            {
                RemoveFailure = null;
            }
            _remoteReadFailure = null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (generation == _remoteRefreshGeneration && ReferenceEquals(remote, _remote))
            {
                RemoveFailure = _remoteReadFailure = _OneLine($"Worktrees unavailable: {error.Message}");
            }
        }
    }

    // AC-1013: Removes a worktree, always after a confirmation. A tree with uncommitted changes gets the...
    [RelayCommand]
    private async Task RemoveAsync(ManagedWorktreeRowViewModel? row)
    {
        if ((_manager is null && _remote is null) || row is null || row.IsOwnerLive)
        {
            return;
        }
        var remote = _remote;

        var (title, message, confirmLabel) = row.HasUncommittedChanges
            ? ("Delete worktree with unsaved changes?",
                $"The worktree on branch '{row.Branch}' has uncommitted changes that will be lost. Its committed history stays on the branch.",
                "Delete anyway")
            : ("Remove worktree?",
                $"Remove the worktree on branch '{row.Branch}'? The branch itself is kept.",
                "Remove");

        if (!await _ConfirmAsync(title, message, confirmLabel))
        {
            return;
        }

        try
        {
            RemoveNotice = await _RemoveAsync(row, row.HasUncommittedChanges, remote) is { } notice
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
        if (_manager is null && _remote is null)
        {
            return;
        }
        var remote = _remote;

        // A missing folder holds no work, even when a restored pane still claims its registry row.
        List<string> refusals = [];
        List<string> notices = [];
        foreach (var row in Worktrees.Where(worktree => worktree.Exists == false
            || ((worktree.IsClean || worktree.NothingToKeep) && worktree.IsOwnerLive == false)).ToList())
        {
            try
            {
                if (await _RemoveAsync(row, force: false, remote) is { } notice)
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

    private bool _CanCreateWorktree() => _remote is not null && SelectedRemoteProject is not null;

    [RelayCommand(CanExecute = nameof(_CanCreateWorktree))]
    private async Task CreateWorktreeAsync()
    {
        if (_remote is not { } remote || SelectedRemoteProject is not { } project)
        {
            return;
        }
        try
        {
            await remote.CreateWorktreeAsync(project.Id, NewWorktreeLabel);
            NewWorktreeLabel = null;
            RemoveFailure = null;
            await RefreshAsync();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RemoveFailure = _OneLine($"Could not create the worktree: {error.Message}");
        }
    }

    private async Task<bool> _ConfirmAsync(string title, string message, string confirmLabel)
    {
        if (_confirm is not { } confirm)
        {
            return await (_dialogs?.ShowConfirmationDialogAsync(title, message, confirmLabel) ?? Task.FromResult(false));
        }
        try
        {
            return await UiThreadCall.Uncapped(() => confirm(title, message, confirmLabel));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RemoveFailure = _OneLine($"The worktree was not removed because confirmation is unavailable: {error.Message}");
            return false;
        }
    }

    private async Task<string?> _RemoveAsync(ManagedWorktreeRowViewModel row, bool force, IRemoteWorktrees? remote)
    {
        if (row.Remote is { } worktree)
        {
            await (remote ?? throw new InvalidOperationException("The worktree's server connection is unavailable.")).RemoveWorktreeAsync(worktree, force);
            return null;
        }
        return await (_manager ?? throw new InvalidOperationException("No worktree manager is available.")).RemoveAsync(row.Record, force);
    }

    // AC-1013: git says why across several lines; the dialog shows it on one. Beyond reading better in a...
    private static string _OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
