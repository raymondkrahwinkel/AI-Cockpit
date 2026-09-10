using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

// AC-493: on-disk shape of a `JobRecurrence`, written only for a job that has one. `DayOfWeek` round-trips as its
// name through the shared `JsonStringEnumConverter`, so a hand-edited config reads "Monday" rather than "1".
internal sealed class JobRecurrenceEntry
{
    public int WeekOfMonth { get; set; }

    public DayOfWeek DayOfWeek { get; set; }

    public static JobRecurrenceEntry FromDomain(JobRecurrence recurrence) => new()
    {
        WeekOfMonth = recurrence.WeekOfMonth,
        DayOfWeek = recurrence.DayOfWeek,
    };

    public JobRecurrence ToDomain() => new(WeekOfMonth, DayOfWeek);
}
