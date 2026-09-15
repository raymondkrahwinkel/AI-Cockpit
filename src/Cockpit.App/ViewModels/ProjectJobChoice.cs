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

    // AC-490: the most recent session started from this job, or null when the trail has none — which is not "never
    // ran": nothing was recorded before the trail existed, so its absence says nothing and the line says nothing.
    public ProjectJobRun? LastRun { get; init; }

    // AC-493 criterion 3: the rule and the date it last came round, read from the rule and never from the day the
    // cockpit opened. "last on 10 August" and not "due since": nothing here knows whether the work was done, and
    // that phrasing accuses an operator who did it on the day. Invariant culture, as the labels around it are too.
    public string? RecurrenceLine
    {
        get
        {
            var parts = new List<string>(2);
            if (Job.Recurrence is { } recurrence)
            {
                parts.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{recurrence.Describe()} · last on {recurrence.LastOccurrenceOn(Today):d MMMM}"));
            }

            // AC-490: the one thing the host saw of a run is its start, so that is the one thing said — never "done".
            if (LastRun is { } run)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"started {run.StartedAt:d MMMM}"));
            }

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    // AC-490: what the agent said it did with the work, labelled as its account so it is never read as something the
    // host measured. Null until a run's agent reports, and then that report verbatim.
    public string? AgentSummaryLine => LastRun?.AgentSummary is { Length: > 0 } summary
        ? $"according to the agent: {summary}"
        : null;
}
