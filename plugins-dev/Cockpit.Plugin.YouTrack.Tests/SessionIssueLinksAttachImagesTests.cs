using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>The per-session "attach sent images to the issue" choice (AC-14): off by default, per pane, and cleared when the pane stops tracking an issue.</summary>
public class SessionIssueLinksAttachImagesTests
{
    [Fact]
    public void AttachesImages_DefaultsOff()
    {
        new SessionIssueLinks().AttachesImages("pane-1").Should().BeFalse();
    }

    [Fact]
    public void SetAttachesImages_TogglesPerPane()
    {
        var links = new SessionIssueLinks();

        links.SetAttachesImages("pane-1", true);

        links.AttachesImages("pane-1").Should().BeTrue();
        links.AttachesImages("pane-2").Should().BeFalse();

        links.SetAttachesImages("pane-1", false);
        links.AttachesImages("pane-1").Should().BeFalse();
    }

    [Fact]
    public void SetAttachesImages_RaisesChangedForThatPane()
    {
        var links = new SessionIssueLinks();
        string? changedPane = null;
        links.Changed += (_, pane) => changedPane = pane;

        links.SetAttachesImages("pane-1", true);

        changedPane.Should().Be("pane-1");
    }

    [Fact]
    public void Unlink_ClearsTheAttachChoice()
    {
        var links = new SessionIssueLinks();
        links.SetAttachesImages("pane-1", true);

        links.Unlink("pane-1");

        links.AttachesImages("pane-1").Should().BeFalse();
    }

    [Fact]
    public void Link_ToADifferentIssue_ClearsTheAttachChoice()
    {
        var links = new SessionIssueLinks();
        links.Link("pane-1", _Link("AC-1"));
        links.SetAttachesImages("pane-1", true);

        // Re-pointing the pane at another issue must not carry the opt-in over to it.
        links.Link("pane-1", _Link("AC-2"));

        links.AttachesImages("pane-1").Should().BeFalse();
    }

    [Fact]
    public void Link_ToTheSameIssue_KeepsTheAttachChoice()
    {
        var links = new SessionIssueLinks();
        links.Link("pane-1", _Link("AC-1"));
        links.SetAttachesImages("pane-1", true);

        links.Link("pane-1", _Link("AC-1"));

        links.AttachesImages("pane-1").Should().BeTrue();
    }

    private static LinkedIssue _Link(string idReadable) =>
        new(
            new YouTrackInstance("Work", "https://yt.example.com", "token", "AC"),
            new YouTrackIssue("id-" + idReadable, idReadable, "Summary", null, "AC", null));
}
