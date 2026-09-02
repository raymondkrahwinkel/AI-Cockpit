using Cockpit.Plugins.Abstractions;
namespace Cockpit.Plugin.Autopilot.Tests;

// `AutopilotRun.FromIntent` — the trigger payload (AC-150) a tracker sends becomes the run the surface shows.
public class AutopilotRunTests
{
    private static PluginIntent Intent(IReadOnlyDictionary<string, string> data, string caller = "youtrack") =>
        new(caller, "autopilot", "start", data);

    [Fact]
    public void FromIntent_ReadsTheWholePayload_IncludingWhatLaterPhasesNeed()
    {
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string>
        {
            ["tracker"] = "youtrack",
            ["issue"] = "AC-150",
            ["title"] = "Autopilot b — trigger",
            ["stage"] = "Ready",
            ["url"] = "https://example/7",
            ["repository"] = "owner/repo",
        }));

        Assert.Equal("youtrack", run.Tracker);
        Assert.Equal("AC-150", run.IssueId);
        Assert.Equal("Autopilot b — trigger", run.Title);
        Assert.Equal("Ready", run.Stage);
        // Everything else rides along untouched, for the phases that come after the trigger.
        Assert.Equal("https://example/7", run.Data["url"]);
        Assert.True(run.Data.ContainsKey("repository"));
    }

    [Fact]
    public void FromIntent_WithOnlyAnIssue_FallsBackRatherThanLeavingAFieldNull()
    {
        // What a tracker plugin older than this Autopilot sends. The tracker falls back to the calling plugin's own
        // id, and the stage arrives as a value the start gate can judge rather than as null — the gate refuses on it.
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string> { ["issue"] = "42" }, caller: "github-issues"));

        Assert.Equal("github-issues", run.Tracker);
        Assert.Empty(run.Title);
        Assert.Equal(string.Empty, run.Stage);
    }

    // AC-189: the tracker-triggered path must carry the item's url through to the plan source, so a template's
    // {{issue.url}} fills from the real link instead of staying blank — and stays blank, not null, when the trigger
    // carried no link at all.
    public static IEnumerable<object[]> Triggers() =>
    [
        [
            new Dictionary<string, string>
            {
                ["issue"] = "AC-138", ["title"] = "Reading levels", ["url"] = "https://youtrack.example/issue/AC-138",
            },
            "https://youtrack.example/issue/AC-138",
        ],
        [new Dictionary<string, string> { ["issue"] = "AC-138", ["title"] = "Reading levels" }, ""],
    ];

    [Theory]
    [MemberData(nameof(Triggers))]
    public void FromRun_CarriesTheUrl_SoIssueUrlResolvesFromTheTriggeringItem(Dictionary<string, string> data, string expected)
    {
        var source = AutopilotPlanSource.FromRun(AutopilotRun.FromIntent(Intent(data)));

        Assert.NotNull(source);
        Assert.Equal(expected, source!.Url);
    }
}
