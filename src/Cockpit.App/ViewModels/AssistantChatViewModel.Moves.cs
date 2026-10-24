using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

// Assistant threads (§ 9.6): a row dragged onto a thread in the rail, or moved there from its menu, and the toast that
// says what happened and offers to take it back.
public sealed partial class AssistantChatViewModel
{
    // How long the toast stays, and with it Undo and Ctrl+Z.
    private static readonly TimeSpan MoveToastTime = TimeSpan.FromSeconds(8);

    private DispatcherTimer? _moveToastTimer;

    private bool _overlayOpenedForDrag;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDraggingRow))]
    [NotifyPropertyChangedFor(nameof(ThreadSubtitle))]
    private TranscriptEntryViewModel? _draggingRow;

    public bool IsDraggingRow => DraggingRow is not null;

    // Set once the drag has actually been over the chat; a press that never got that far was a click.
    public bool RowDragTravelled { get; set; }

    // What letting go here would do, shown in the thread header while a row is dragged.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThreadSubtitle))]
    private string? _dropHint;

    partial void OnDraggingRowChanged(TranscriptEntryViewModel? value)
    {
        if (value is not null)
        {
            RowDragTravelled = false;
        }

        foreach (var item in ThreadItems)
        {
            item.IsDropCandidate = value is not null && CanDropOn(item);
            item.IsDropHover = false;
        }

        // Where the labels do not fit beside the chat, they come over it for the drag and go again after.
        if (value is not null && RailStand == AssistantRailStand.Icons && !IsRailOverlayOpen)
        {
            _overlayOpenedForDrag = true;
            IsRailOverlayOpen = true;
        }
        else if (value is null && _overlayOpenedForDrag)
        {
            _overlayOpenedForDrag = false;
            IsRailOverlayOpen = false;
        }

        DropHint = null;
    }

    // A thread the row is in on its own already takes nothing; All, Questions and Overview are no thread.
    public bool CanDropOn(AssistantThreadItemViewModel item) => DraggingRow is { } row && item.IsThread
        && !(item.Kind == AssistantThreadItemKind.General
            ? row.IsGeneral
            : row.ThreadProjectIds is [var only] && only == item.ProjectId);

    public void HoverDrop(AssistantThreadItemViewModel? target, bool add)
    {
        foreach (var item in ThreadItems)
        {
            item.IsDropHover = ReferenceEquals(item, target);
        }

        DropHint = target is null ? null : add && target.Kind == AssistantThreadItemKind.Project ? $"Add to {target.Name}" : $"Move to {target.Name}";
    }

    public void Drop(AssistantThreadItemViewModel target, bool add)
    {
        if (DraggingRow is { } row && CanDropOn(target) && Session is { } session)
        {
            session.MoveToThread(row, target.ProjectId, add);
        }
    }

    // --- The toast -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoveToast))]
    [NotifyPropertyChangedFor(nameof(MoveToastText))]
    [NotifyPropertyChangedFor(nameof(CanKeepAlso))]
    [NotifyPropertyChangedFor(nameof(KeepAlsoLabel))]
    private ThreadMove? _lastMove;

    public bool HasMoveToast => LastMove is not null;

    // For screen readers: what was moved, then that it was undone (a polite live region in the view).
    [ObservableProperty]
    private string _moveAnnouncement = string.Empty;

    public string MoveToastText
    {
        get
        {
            if (LastMove is not { } move)
            {
                return string.Empty;
            }

            var target = _ThreadName(move.Target);
            var followed = Session?.RowsThatFollowed(move.Row) ?? 0;
            var with = followed switch
            {
                0 => string.Empty,
                1 => ", with 1 row of its turn",
                _ => $", with {followed} rows of its turn",
            };

            return (move.Added ? $"Added to {target}" : $"Moved to {target}") + with;
        }
    }

    // A move out of a project can still become an add, for whoever did not hold Ctrl.
    public bool CanKeepAlso => LastMove is { Added: false, Target: { } target, PreviousThreads: [var from, ..] } && from != target;

    public string KeepAlsoLabel => LastMove is { PreviousThreads: [var from, ..] } ? $"Keep in {_ThreadName(from)} too" : string.Empty;

    private string _ThreadName(string? projectId) => projectId is null
        ? "General"
        : ThreadItems.FirstOrDefault(item => item.ProjectId == projectId)?.Name ?? projectId;

    // Where the keyboard goes after a move: the row itself while it is still in this thread, else the next row that
    // is, so a keyboard user is not dropped at the top of the list (§ 9.6). Undo brings it back to the row.
    public event Action<TranscriptEntryViewModel>? RowFocusRequested;

    // The row the focus belongs on once `moved` left the open thread, or the row itself while it is still shown.
    internal TranscriptEntryViewModel? FocusAfterMove(TranscriptEntryViewModel moved)
    {
        if (Session is not { } session || session.VisibleTranscript.Contains(moved))
        {
            return moved;
        }

        var index = session.Transcript.IndexOf(moved);
        return session.Transcript.Skip(index + 1).FirstOrDefault(session.VisibleTranscript.Contains)
            ?? session.VisibleTranscript.LastOrDefault();
    }

    private void _OnRowMoved(ThreadMove move)
    {
        if (FocusAfterMove(move.Row) is { } focus)
        {
            RowFocusRequested?.Invoke(focus);
        }

        LastMove = move;
        MoveAnnouncement = $"{MoveToastText}. Press Ctrl+Z to undo.";

        _moveToastTimer?.Stop();
        _moveToastTimer = new DispatcherTimer { Interval = MoveToastTime };
        _moveToastTimer.Tick += (_, _) => DismissMoveToast();
        _moveToastTimer.Start();
    }

    [RelayCommand]
    private void UndoMove()
    {
        if (LastMove is { } move && Session is { } session)
        {
            session.UndoMove(move);
            MoveAnnouncement = "Move undone.";
            RowRevealRequested?.Invoke(move.Row);
            RowFocusRequested?.Invoke(move.Row);
        }

        DismissMoveToast();
    }

    [RelayCommand]
    private void KeepAlso()
    {
        if (LastMove is not { PreviousThreads: [var from, ..] } move || Session is not { } session)
        {
            return;
        }

        // The undo still goes back to before the drag, so the toast keeps the move it started from.
        session.MoveToThread(move.Row, from, add: true, announce: false);
        LastMove = move with { Added = true };
    }

    [RelayCommand]
    private void DismissMoveToast()
    {
        _moveToastTimer?.Stop();
        LastMove = null;
    }
}
