using Cockpit.Plugins.Abstractions;
using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// <see cref="AutopilotRun.FromIntent"/> — the trigger payload (AC-150) a tracker sends becomes the run the surface shows.
/// </summary>
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

        run.Tracker.Should().Be("youtrack");
        run.IssueId.Should().Be("AC-150");
        run.Title.Should().Be("Autopilot b — trigger");
    }

    [Fact]
    public void FromIntent_FallsBackToCallerId_WhenTrackerOmitted()
    {
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string> { ["issue"] = "42" }, caller: "github-issues"));

        run.Tracker.Should().Be("github-issues");
        run.Title.Should().BeEmpty();
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

        run.Data.Should().ContainKey("url").WhoseValue.Should().Be("https://example/7");
        run.Data.Should().ContainKey("repository");
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

        source.Should().NotBeNull();
        source!.Url.Should().Be("https://youtrack.example/issue/AC-138");
    }

    [Fact]
    public void FromRun_LeavesUrlEmpty_WhenTheTriggerCarriesNone()
    {
        var run = AutopilotRun.FromIntent(Intent(new Dictionary<string, string> { ["issue"] = "AC-1" }));

        AutopilotPlanSource.FromRun(run)!.Url.Should().BeEmpty();
    }
}
