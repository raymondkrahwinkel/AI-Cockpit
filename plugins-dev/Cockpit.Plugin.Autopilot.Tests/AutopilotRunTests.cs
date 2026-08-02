using Cockpit.Plugins.Abstractions;
namespace Cockpit.Plugin.Autopilot.Tests;

// `AutopilotRun.FromIntent` — the trigger payload (AC-150) a tracker sends becomes the run the surface shows.
public class AutopilotRunTests
{
    private static PluginIntent Intent(IReadOnlyDictionary<string, string> data, string caller = "youtrack") =>
        new(caller, "autopilot", "start", data);

    [Fact]
    public void FromIntent_ReadsTrackerIssueAndTitle()
    {
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string>
        {
            ["tracker"] = "youtrack",
            ["issue"] = "AC-150",
            ["title"] = "Autopilot b — trigger",
        }));

        Assert.Equal("youtrack", run.Tracker);
        Assert.Equal("AC-150", run.IssueId);
        Assert.Equal("Autopilot b — trigger", run.Title);
    }

    [Fact]
    public void FromIntent_ReadsTheStageTheTrackerReports()
    {
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string>
        {
            ["issue"] = "AC-345",
            ["stage"] = "Ready",
        }));

        Assert.Equal("Ready", run.Stage);
    }

    [Fact]
    public void FromIntent_WithoutAStage_ReadsAsUnknownRatherThanNull()
    {
        // What a tracker plugin older than this Autopilot sends. The start gate refuses on it, so it has to arrive as a
        // value the gate can judge rather than as null.
        Assert.Equal(string.Empty, AutopilotRun.FromIntent(Intent(new Dictionary<string, string> { ["issue"] = "AC-345" })).Stage);
    }

    [Fact]
    public void FromIntent_FallsBackToCallerId_WhenTrackerOmitted()
    {
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string> { ["issue"] = "42" }, caller: "github-issues"));

        Assert.Equal("github-issues", run.Tracker);
        Assert.Empty(run.Title);
    }

    [Fact]
    public void FromIntent_KeepsTheWholePayload_ForLaterPhases()
    {
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string>
        {
            ["issue"] = "owner/repo#7",
            ["url"] = "https://example/7",
            ["repository"] = "owner/repo",
        }));

        Assert.Equal("https://example/7", run.Data["url"]);
        Assert.True(run.Data.ContainsKey("repository"));
    }

    [Fact]
    public void FromRun_CarriesTheUrl_SoIssueUrlResolvesFromTheTriggeringItem()
    {
        // AC-189: the tracker-triggered path must carry the item's url through to the plan source, so a template's
        // {{issue.url}} fills from the real link instead of staying blank.
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string>
        {
            ["issue"] = "AC-138",
            ["title"] = "Reading levels",
            ["url"] = "https://youtrack.example/issue/AC-138",
        }));

        var source = AutopilotPlanSource.FromRun(run);

        Assert.NotNull(source);
        Assert.Equal("https://youtrack.example/issue/AC-138", source!.Url);
    }

    [Fact]
    public void FromRun_LeavesUrlEmpty_WhenTheTriggerCarriesNone()
    {
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string> { ["issue"] = "AC-1" }));

        Assert.Empty(AutopilotPlanSource.FromRun(run)!.Url);
    }
}
