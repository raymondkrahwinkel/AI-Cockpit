using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Delegation;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The cockpit's view on delegated work (#67): every task another session handed to a profile, what it is doing,
/// what it answered, and a way to stop it. A delegated task runs a real session with no tab of its own, so this
/// is the only place it is visible — and the operator keeps the final say over anything running in their name.
/// </summary>
public sealed partial class DelegatedTasksViewModel : ObservableObject, ISingletonService
{
    private readonly IDelegationService? _delegation;

    public DelegatedTasksViewModel()
    {
        // Design-time/previewer: sample rows and an output so the whole view renders — including the detail pane
        // and the Stop button — without a live engine behind it.
        Tasks.Add(new DelegatedTaskRowViewModel(_Sample("summarise the changelog", DelegatedTaskStatus.Running)));
        Tasks.Add(new DelegatedTaskRowViewModel(_Sample("rename the config keys", DelegatedTaskStatus.Completed, "Renamed 14 keys across 6 files.")));
        _designOutput = "Here is the merged function:\n\n```python\ndef merge_intervals(intervals):\n    intervals.sort()\n    return intervals\n```\n\nIt sorts by start and folds overlapping ranges.";
        _RebuildGroups();
        SelectedTask = Tasks[0];
    }

    /// <summary>Design-time only: the detail pane has no engine to read output from in the previewer.</summary>
    private readonly string? _designOutput;

    public DelegatedTasksViewModel(IDelegationService delegation)
    {
        _delegation = delegation;

        // The engine raises this off the UI thread (it is driven by a session's event pump), so the refresh is
        // marshalled here rather than assuming the caller is on the dispatcher.
        _delegation.TasksChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<DelegatedTaskRowViewModel> Tasks { get; } = [];

    /// <summary>
    /// The tasks grouped by what they are doing — waiting for a slot, working, finished — so the list reads as a
    /// picture of the work rather than one flat pile where a queued task looks the same as a finished one.
    /// </summary>
    public ObservableCollection<DelegatedTaskGroupViewModel> Groups { get; } = [];

    /// <summary>True while any delegated task is running or waiting for a slot — drives the "N background task(s)" hint.</summary>
    public bool HasActiveTasks => Tasks.Any(task => task.IsActive);

    /// <summary>How many delegated tasks are running or queued right now.</summary>
    public int ActiveTaskCount => Tasks.Count(task => task.IsActive);

    /// <summary>
    /// Which brush the status bar's count reads in: the working blue while something is running, quiet grey while
    /// nothing is. The count is always on screen — knowing that nothing is running on your behalf is worth as much as
    /// knowing that something is — so it is the colour, not the presence, that says which.
    /// </summary>
    public string ActiveTaskBrushKey => HasActiveTasks ? "CockpitStatusBusyBrush" : "CockpitTextFaintBrush";

    [ObservableProperty]
    private DelegatedTaskRowViewModel? _selectedTask;

    /// <summary>The selected task's output, read-only: what the sub-agent produced, for an operator checking on it.</summary>
    public string SelectedTaskOutput => SelectedTask is null
        ? string.Empty
        : _delegation is null ? _designOutput ?? string.Empty : _BuildOutput(SelectedTask.TaskId);

    partial void OnSelectedTaskChanged(DelegatedTaskRowViewModel? value) => OnPropertyChanged(nameof(SelectedTaskOutput));

    [RelayCommand]
    public void Refresh()
    {
        if (_delegation is null)
        {
            return;
        }

        var selectedId = SelectedTask?.TaskId;

        Tasks.Clear();
        foreach (var task in _delegation.ListTasks())
        {
            Tasks.Add(new DelegatedTaskRowViewModel(task));
        }

        _RebuildGroups();

        SelectedTask = Tasks.FirstOrDefault(task => task.TaskId == selectedId) ?? Tasks.FirstOrDefault();
        OnPropertyChanged(nameof(HasActiveTasks));
        OnPropertyChanged(nameof(ActiveTaskCount));
        OnPropertyChanged(nameof(ActiveTaskBrushKey));
        OnPropertyChanged(nameof(SelectedTaskOutput));
    }

    /// <summary>Stops a delegated task on the operator's say-so — the same stop path the orchestrator's own stop_task takes.</summary>
    [RelayCommand]
    private async Task StopAsync(DelegatedTaskRowViewModel? task)
    {
        if (_delegation is null || task is null)
        {
            return;
        }

        await _delegation.StopAsync(task.TaskId);
        Refresh();
    }

    private void _RebuildGroups()
    {
        Groups.Clear();
        foreach (var group in DelegatedTaskGroupViewModel.From(Tasks).Where(group => group.HasTasks))
        {
            Groups.Add(group);
        }
    }

    private string _BuildOutput(string taskId)
    {
        var task = _delegation!.GetTask(taskId);
        if (task is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(task.Error))
        {
            return $"Failed: {task.Error}";
        }

        if (!string.IsNullOrWhiteSpace(task.Result))
        {
            return task.Result;
        }

        // Still working: show the steps so far rather than an empty pane, so a long task does not look stuck.
        var (events, _, _) = _delegation.GetOutput(taskId);
        var lines = events
            .Select(evt => evt switch
            {
                Core.Sessions.AssistantTextCompleted text => text.Text,
                Core.Sessions.ToolUseRequested tool => $"· {tool.ToolName}",
                Core.Sessions.SessionError error => $"Error: {error.Message}",
                _ => null,
            })
            .Where(line => !string.IsNullOrWhiteSpace(line));

        var output = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(output) ? "Working…" : output;
    }

    private static DelegatedTaskView _Sample(string label, DelegatedTaskStatus status, string? result = null) => new(
        Guid.NewGuid().ToString("N"),
        ProfileLabel: "local (Ollama)",
        Label: label,
        TaskType: null,
        status,
        DateTimeOffset.Now.AddMinutes(-3),
        DateTimeOffset.Now.AddMinutes(-3),
        status == DelegatedTaskStatus.Completed ? DateTimeOffset.Now : null,
        TurnCount: 1,
        result,
        Error: null);
}
