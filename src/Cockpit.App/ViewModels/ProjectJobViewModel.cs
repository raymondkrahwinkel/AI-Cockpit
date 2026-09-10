using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// AC-491: one editable job in the project editor — what it does, and the line saying what it changes. An
// untouched row is dropped on save; a half-filled one is refused there (see `ProjectDialogViewModel.SaveAsync`),
// since a job that cannot say what it touches is the one thing this list must never offer.
public partial class ProjectJobViewModel(
    string prompt = "",
    string blastRadius = "",
    JobRecurrence? recurrence = null) : ViewModelBase
{
    [ObservableProperty]
    private string _prompt = prompt;

    [ObservableProperty]
    private string _blastRadius = blastRadius;

    // AC-493: an index into `WeekChoices`, where 0 is "does not repeat" and anything else is the week of the month
    // the job falls in. One number rather than a flag beside a number, because it is one choice for the operator.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Repeats))]
    private int _recurrenceWeek = recurrence?.WeekOfMonth ?? 0;

    [ObservableProperty]
    private DayOfWeek _recurrenceDay = recurrence?.DayOfWeek ?? DayOfWeek.Monday;

    // Index 0 is the way out: a job that does not come round is the default, and stays what it always was.
    public IReadOnlyList<string> WeekChoices { get; } =
        ["does not repeat", "first", "second", "third", "fourth", "fifth"];

    // Monday first rather than `Enum.GetValues`' Sunday, since these are the weeks of someone's working month.
    public IReadOnlyList<DayOfWeek> DayChoices { get; } =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday,
    ];

    public bool Repeats => RecurrenceWeek > 0;

    public ProjectJob ToDomain() => new(
        Prompt.Trim(),
        BlastRadius.Trim(),
        Repeats ? new JobRecurrence(RecurrenceWeek, RecurrenceDay) : null);
}
