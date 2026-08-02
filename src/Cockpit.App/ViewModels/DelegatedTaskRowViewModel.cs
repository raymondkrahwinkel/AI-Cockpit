using Cockpit.Core.Delegation;

namespace Cockpit.App.ViewModels;

// One delegated task (#67) as the task list shows it: who is doing what, how far it is, and whether it can still be stopped.
public sealed class DelegatedTaskRowViewModel
{
    public DelegatedTaskRowViewModel(DelegatedTaskView task)
    {
        TaskId = task.TaskId;
        Profile = task.ProfileLabel;
        Title = string.IsNullOrWhiteSpace(task.Label) ? task.TaskType ?? "Delegated task" : task.Label;
        Status = task.Status;
        StartedAt = task.StartedAt;
        TurnCount = task.TurnCount;
    }

    public string TaskId { get; }

    public string Profile { get; }

    public string Title { get; }

    public DelegatedTaskStatus Status { get; }

    public DateTimeOffset? StartedAt { get; }

    public int TurnCount { get; }

    public string StatusText => Status.ToString();

    // A task that is still running or waiting for a slot — the ones the operator may want to stop.
    public bool IsActive => Status is DelegatedTaskStatus.Running or DelegatedTaskStatus.Queued;

    // Stop only makes sense while the task is still going; a finished task shows no button rather than a dead one.
    public bool CanStop => IsActive;

    public string StartedText => StartedAt is { } startedAt ? startedAt.ToLocalTime().ToString("HH:mm") : "—";

    // How many turns the delegated session has completed — a follow-up shows up here as the count going up.
    public string TurnsText => TurnCount == 1 ? "1 turn" : $"{TurnCount} turns";

    // The dot beside the task, in the same colours the session sidebar uses: working, waiting, done, or wrong.
    // Keyed rather than a brush so the view model stays free of Avalonia types, like the profile rows do.
    public string StatusBrushKey => Status switch
    {
        DelegatedTaskStatus.Running => "CockpitStatusBusyBrush",
        DelegatedTaskStatus.Queued => "CockpitStatusWaitingBrush",
        DelegatedTaskStatus.Completed => "CockpitStatusDoneBrush",
        DelegatedTaskStatus.Failed => "CockpitStatusErrorBrush",

        // Stopped is deliberately not the waiting colour: a task you cancelled would otherwise look exactly like
        // one that is still about to run, which is the confusion the grouping is meant to remove.
        _ => "CockpitTextFaintBrush",
    };
}
