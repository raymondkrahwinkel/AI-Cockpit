using System.Collections.ObjectModel;
using Cockpit.Core.Delegation;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One band of the delegated-tasks list (#67): the tasks that are working, the ones waiting for a slot, and the
/// ones that are done. Grouping is what makes the list readable — in a flat list a queued task and a finished one
/// look alike, while the whole question an operator has is "what is running, and what is still coming".
/// </summary>
public sealed class DelegatedTaskGroupViewModel
{
    public DelegatedTaskGroupViewModel(string title, IEnumerable<DelegatedTaskRowViewModel> tasks)
    {
        Title = title;
        Tasks = new ObservableCollection<DelegatedTaskRowViewModel>(tasks);
    }

    public string Title { get; }

    public ObservableCollection<DelegatedTaskRowViewModel> Tasks { get; }

    public string Header => $"{Title} ({Tasks.Count})";

    /// <summary>An empty band is hidden rather than shown as a bare header with nothing under it.</summary>
    public bool HasTasks => Tasks.Count > 0;

    /// <summary>Builds the bands in the order an operator scans them: what is running now, what is next, what is done.</summary>
    public static IEnumerable<DelegatedTaskGroupViewModel> From(IEnumerable<DelegatedTaskRowViewModel> tasks)
    {
        var all = tasks.ToList();

        yield return new DelegatedTaskGroupViewModel(
            "Running",
            all.Where(task => task.Status == DelegatedTaskStatus.Running));

        yield return new DelegatedTaskGroupViewModel(
            "Queued",
            all.Where(task => task.Status == DelegatedTaskStatus.Queued));

        yield return new DelegatedTaskGroupViewModel(
            "Finished",
            all.Where(task => task.Status is DelegatedTaskStatus.Completed or DelegatedTaskStatus.Failed or DelegatedTaskStatus.Stopped));
    }
}
