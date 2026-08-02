using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// One row in the background-work pop-out (AC-531): a sub-agent or shell that outlived its turn. `AgeText`
// is derived, not reported — the CLI's own `BackgroundTask` carries no start time, so
// `SessionViewModel` stamps the moment it first saw this `TaskId` in a
// `BackgroundTasksChanged` snapshot and this row counts up from there rather than from a real start time.
public sealed class BackgroundTaskViewModel : ObservableObject
{
    private readonly DateTimeOffset _firstSeenAt;
    private string? _description;
    private bool _isSelected;

    public BackgroundTaskViewModel(string taskId, BackgroundTaskKind kind, string? description, DateTimeOffset firstSeenAt)
    {
        TaskId = taskId;
        Kind = kind;
        _description = description;
        _firstSeenAt = firstSeenAt;
    }

    public string TaskId { get; }

    public BackgroundTaskKind Kind { get; }

    // Falls back to a label rather than rendering blank — the CLI's own description is optional.
    public string Description => string.IsNullOrEmpty(_description) ? "(no description)" : _description;

    public string KindLabel => Kind switch
    {
        BackgroundTaskKind.Shell => "Shell",
        BackgroundTaskKind.SubAgent => "Sub-agent",
        _ => "Background task",
    };

    // Kind-coded, not a real per-task status — the contract carries no status field. Sub-agents read as the busy
    // colour and shells as the waiting colour, matching the row colouring in the approved mockup (AC-531).
    public string StatusBrushKey => Kind switch
    {
        BackgroundTaskKind.Shell => "CockpitStatusWaitingBrush",
        BackgroundTaskKind.SubAgent => "CockpitStatusBusyBrush",
        _ => "CockpitTextFaintBrush",
    };

    // "m:ss" since this pane first saw the task — the same notation and the same
    // `SessionViewModel._FormatElapsed` AC-532's composer activity band already shipped, so this
    // pop-out follows it rather than inventing its own (AC-531 #8).
    public string AgeText => SessionViewModel._FormatElapsed(DateTimeOffset.Now - _firstSeenAt);

    // True while this row's detail is expanded in the pop-out — only one row at a time (AC-531 #4).
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // The driver restates the whole set on every snapshot, so a still-running task's description can
    // legitimately change between two reports — this keeps the row current instead of freezing on the first one.
    internal void UpdateDescription(string? description) => SetProperty(ref _description, description, nameof(Description));

    // Re-raises `AgeText`'s change notification — called on the same view-owned tick
    // `SessionViewModel.RefreshActiveToolActivityAge` uses, so the pop-out's elapsed time visibly
    // counts up instead of freezing at whatever it read on first render.
    internal void RaiseAgeChanged() => OnPropertyChanged(nameof(AgeText));
}
