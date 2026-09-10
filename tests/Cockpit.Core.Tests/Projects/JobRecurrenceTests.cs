using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// AC-493 criterion 3: when a recurring job last came round. The date is read from the rule and never from the
/// day the cockpit happened to open, which is the whole of the criterion — a monthly task whose date quietly
/// moves up to today is a period nobody knows was skipped, and it looks perfectly ordinary on screen.
/// </summary>
public class JobRecurrenceTests
{
    [Fact]
    public void TheLastOccurrence_IsReadFromTheRule_NotFromTheDayTheCockpitOpened()
    {
        var secondMonday = new JobRecurrence(2, DayOfWeek.Monday);

        // Opened on 3 September having been closed since August: August's second Monday, not September's — which
        // is still ahead — and not the day it opened.
        Assert.Equal(new DateOnly(2026, 8, 10), secondMonday.LastOccurrenceOn(new DateOnly(2026, 9, 3)));

        // On the day itself, and the day after. An occurrence counts from its own date, so `<` in place of `<=`
        // would put both of these a month early.
        Assert.Equal(new DateOnly(2026, 9, 14), secondMonday.LastOccurrenceOn(new DateOnly(2026, 9, 14)));
        Assert.Equal(new DateOnly(2026, 9, 14), secondMonday.LastOccurrenceOn(new DateOnly(2026, 9, 15)));

        // A month opening on the rule's own weekday — 1 June 2026 is a Monday — is where an off-by-one in the
        // week offset lands a week out without anything else noticing.
        Assert.Equal(new DateOnly(2026, 6, 8), secondMonday.LastOccurrenceOn(new DateOnly(2026, 6, 30)));

        // July 2026 has only four Mondays. A fifth-week rule walks back to a month that has one rather than
        // running past the end of the month into the next.
        Assert.Equal(
            new DateOnly(2026, 6, 29),
            new JobRecurrence(5, DayOfWeek.Monday).LastOccurrenceOn(new DateOnly(2026, 7, 31)));

        // A week a month cannot have is clamped rather than refused, because a hand-edited config can write any
        // number here. Without the clamp the walk back through months finds nothing and never ends — a hang, not
        // a wrong date, which is why it is asserted here and not only on the property.
        Assert.Equal(
            new DateOnly(2026, 9, 7),
            new JobRecurrence(0, DayOfWeek.Monday).LastOccurrenceOn(new DateOnly(2026, 9, 10)));
    }
}
