using System.Globalization;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// AC-491: a job together with the project offering it. A button hands its command one parameter, and starting a
// job needs both — the project for the folder, profile and servers, the job for the prompt.
public sealed record ProjectJobChoice(Project Project, ProjectJob Job)
{
    // AC-493: replaceable so a test can stand on a chosen day — a reminder that can only be checked by waiting for
    // a real second Monday is a reminder nobody tests. Production leaves it, and gets today.
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    // AC-493 criterion 3: the rule and the date it last came round, read from the rule and never from the day the
    // cockpit opened. "last on 10 August" and not "due since": nothing here knows whether the work was done, and
    // that phrasing accuses an operator who did it on the day. Invariant culture, as the labels around it are too.
    public string? RecurrenceLine => Job.Recurrence is { } recurrence
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{recurrence.Describe()} · last on {recurrence.LastOccurrenceOn(Today):d MMMM}")
        : null;
}
