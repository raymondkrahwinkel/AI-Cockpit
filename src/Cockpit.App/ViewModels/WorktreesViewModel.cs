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
        Worktrees.Add(new ManagedWorktreeRowViewModel(new WorktreeStatus(record, Exists: true, HasUncommittedChanges: true, StrandableCommits: 0), isOwnerLive: false));
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

    /// <summary>
    /// Why the last removal did not go through, in git's own words — null while nothing has failed. Shown in the
    /// dialog because a row that stays put is otherwise indistinguishable from a button that does nothing (AC-342).
    /// </summary>
    [ObservableProperty]
    private string? _removeFailure;

    public bool HasRemoveFailure => RemoveFailure is not null;

    /// <summary>
    /// What a successful removal left behind (AC-507) — for example a worktree folder abandoned on disk because its
    /// repository was gone. Distinct from <see cref="RemoveFailure"/>: the removal went through, this is information
    /// about it, not a reason it failed. Null when the last removal had nothing to mention.
    /// </summary>
    [ObservableProperty]
    private string? _removeNotice;

    public bool HasRemoveNotice => RemoveNotice is not null;

    /// <summary>Supplied by the cockpit: the ids of the sessions alive right now, so each worktree's owner shows as live or gone.</summary>
    public Func<IReadOnlySet<string>>? LiveSessionIds { get; set; }

    /// <summary>
    /// Supplied by the cockpit (AC-520): the display names of the sessions, by pane id — the live pane's title,
    /// else the persisted <c>WorkspacePane.Title</c> for one that no longer has a pane, so a row still names its
    /// owner after the session has closed or crashed. Taken as one snapshot alongside <see cref="LiveSessionIds"/>,
    /// on the same thread and at the same moment: both read the cockpit's live collections.
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>>? SessionNames { get; set; }

    /// <summary>
    /// Supplied by the cockpit (AC-520 fix 6): the pane ids that currently show an open restore offer (AC-410) —
    /// live only on the strength of that offer, with nothing actually running behind it. Taken as one snapshot
    /// alongside <see cref="LiveSessionIds"/> and <see cref="SessionNames"/>, on the same thread and at the same
    /// moment, so a row can never disagree with itself about why its owner counts as live.
    /// </summary>
    public Func<IReadOnlySet<string>>? RestoreOfferPaneIds { get; set; }

    /// <summary>Raised when the operator reattaches to a gone worktree; the cockpit starts a new session in it.</summary>
    public event Action<WorktreeRecord>? ReattachRequested;

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasWorktrees));
        OnPropertyChanged(nameof(CountBrushKey));
    }

    partial void OnRemoveFailureChanged(string? value) => OnPropertyChanged(nameof(HasRemoveFailure));

    partial void OnRemoveNoticeChanged(string? value) => OnPropertyChanged(nameof(HasRemoveNotice));

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

    /// <summary>
    /// Removes a worktree, always after a confirmation. A tree with uncommitted changes gets the stronger consent
    /// that names the loss (its committed history stays on the branch; only unsaved edits go); a clean one gets a
    /// plain confirm. Never removes a tree a live session is still on — that would pull the working directory out
    /// from under a running session; close the session first.
    /// </summary>
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

    /// <summary>
    /// Gives up a worktree's claim on a session that is only "live" because of an open restore offer with nothing
    /// running behind it (AC-520 fix 6, Raymond's explicit choice over a time-based expiry on the offer itself: "ik
    /// ben wel voor die geef vrij actie in het paneel ... legt de keuze bij de gebruiker neer"). Detaches ownership
    /// only — no worktree is removed here — so the row becomes an ordinary orphan afterwards: Remove and Reattach
    /// become available, and the operator picks from there.
    /// </summary>
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

    /// <summary>Removes every worktree that is safe to remove — clean or already gone, no work to lose. Never touches one with unsaved changes.</summary>
    [RelayCommand]
    private async Task CleanUpFinishedAsync()
    {
        if (_manager is null)
        {
            return;
        }

        // Only clean trees whose session is gone: a live session's tree is never pulled from under it, even when clean.
        // A tree with no working copy left counts as one of them (AC-342) — its folder disappeared, or the folder is
        // still there with the checkout cleared out of it: there is nothing left to lose, and removing it keeps the
        // branch, so all that goes is the registry entry. Neither reads as clean — IsClean is a measurement, and
        // nothing about a tree that is not there can be measured — which is why NothingToKeep is named here rather
        // than folded into that meaning.
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

    // git says why across several lines; the dialog shows it on one. Beyond reading better in a single status line,
    // a wrapping TextBlock over text that still holds newlines is the Avalonia 12.0.5 defect that took the prompt
    // preview out with an OutOfMemoryException (AC-292) — the wrapper never advances and allocates empty lines until
    // memory runs out. Flattening here keeps that class of text away from the wrap.
    // The separators go in as an array on purpose: passing them as two arguments binds to Split(char, int,
    // StringSplitOptions) — the second separator silently becoming a count — and nothing splits on newlines at all.
    private static string _OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
