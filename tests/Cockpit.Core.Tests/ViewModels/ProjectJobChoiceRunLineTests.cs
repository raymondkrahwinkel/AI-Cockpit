using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-490 criterion 4, on top of AC-493's rule: the job's line says only what the system knows. With a run it adds
/// when that run was started — the one thing the host saw — and passes the agent's summary on labelled as the
/// agent's. Without a run the line is exactly what AC-493 drew: no "never ran", because nothing was recorded before
/// the trail existed, and no "done", because the host cannot see an end.
/// </summary>
public class ProjectJobChoiceRunLineTests
{
    [Fact]
    public void TheJobLine_AddsOnlyWhenARunStarted_AndLabelsTheSummaryAsTheAgents()
    {
        var job = new ProjectJob("Process this month's invoices", "changes nothing · reports only", new JobRecurrence(2, DayOfWeek.Monday));
        var project = Project.Create("Invoices") with { Jobs = [job] };
        var today = new DateOnly(2026, 9, 11);

        // No run recorded: the AC-493 line, unchanged, and no summary line at all.
        var withoutRun = new ProjectJobChoice(project, job) { Today = today };
        Assert.Equal("second Monday of the month · last on 10 August", withoutRun.RecurrenceLine);
        Assert.Null(withoutRun.AgentSummaryLine);

        // A run the agent has not reported on yet: the start is known, so it is said; nothing else is.
        var startedAt = new DateTimeOffset(2026, 9, 9, 10, 15, 0, TimeSpan.FromHours(2));
        var silentRun = new ProjectJobRun("pane-1", project.Id, job.Id, startedAt, LastReportedAt: null, AgentSummary: null);
        var withSilentRun = new ProjectJobChoice(project, job) { Today = today, LastRun = silentRun };
        Assert.Equal("second Monday of the month · last on 10 August · started 9 September", withSilentRun.RecurrenceLine);
        Assert.Null(withSilentRun.AgentSummaryLine);

        // A run the agent reported on: the summary appears, and only as the agent's account — the words "done" or
        // "finished" appear nowhere the host speaks, whatever the agent claimed.
        var reportedRun = silentRun with { LastReportedAt = startedAt.AddMinutes(40), AgentSummary = "22 of 22 invoices read, all done" };
        var withReportedRun = new ProjectJobChoice(project, job) { Today = today, LastRun = reportedRun };
        Assert.Equal("according to the agent: 22 of 22 invoices read, all done", withReportedRun.AgentSummaryLine);
        Assert.DoesNotContain("done", withReportedRun.RecurrenceLine, StringComparison.OrdinalIgnoreCase);

        // A job that does not come round still gets its start line; a job with neither rule nor run gets nothing.
        var plainJob = job with { Recurrence = null };
        Assert.Equal("started 9 September", new ProjectJobChoice(project, plainJob) { Today = today, LastRun = silentRun }.RecurrenceLine);
        Assert.Null(new ProjectJobChoice(project, plainJob) { Today = today }.RecurrenceLine);
    }
}
