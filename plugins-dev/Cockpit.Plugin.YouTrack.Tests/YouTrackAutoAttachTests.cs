using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>The AC-116 automatic image-attach: which tool calls trigger it, what issue it reads from a result, which instance it resolves, and that a turn's images are attached once per issue.</summary>
public class YouTrackAutoAttachTests
{
    [Theory]
    [InlineData("mcp__youtrack_personal__create_issue", true)]
    [InlineData("mcp__youtrack_personal__update_issue", true)]
    [InlineData("mcp__YouTrack__Personal__create_issue", true)]
    [InlineData("mcp__youtrack__create_draft_issue", false)] // a draft is not yet an issue to attach to
    [InlineData("mcp__youtrack__get_issue", false)]
    [InlineData("mcp__github__create_issue", false)] // another tracker's create_issue is not ours
    [InlineData("Bash", false)]
    [InlineData("", false)]
    public void IsIssueCreateOrUpdate_MatchesOnlyYouTrackCreateOrUpdate(string toolName, bool expected)
    {
        YouTrackToolActivity.IsIssueCreateOrUpdate(toolName).Should().Be(expected);
    }

    [Fact]
    public void TryParse_ReadsIssueIdAndHostFromACreateResult()
    {
        var target = YouTrackToolResultParser.TryParse("""{"issueId":"AC-9","url":"https://yt.example.com/youtrack/issue/AC-9"}""");

        target.Should().NotBeNull();
        target!.IssueId.Should().Be("AC-9");
        target.Host.Should().Be("yt.example.com");
    }

    [Fact]
    public void TryParse_ReadsIssueIdWithNoHostFromAnUpdateResult()
    {
        var target = YouTrackToolResultParser.TryParse("""{"issueId":"AC-9","updatedFields":["Stage"]}""");

        target.Should().NotBeNull();
        target!.IssueId.Should().Be("AC-9");
        target.Host.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"idReadable":"AC-9"}""", "AC-9")]
    [InlineData("""{"id":"3-22"}""", "3-22")]
    public void TryParse_FallsBackAcrossIdFieldNames(string json, string expectedId)
    {
        YouTrackToolResultParser.TryParse(json)!.IssueId.Should().Be(expectedId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"updatedFields":["Stage"]}""")] // no id, no url
    [InlineData("")]
    public void TryParse_ReturnsNullWhenThereIsNoIssue(string content)
    {
        YouTrackToolResultParser.TryParse(content).Should().BeNull();
    }

    [Fact]
    public void TryParse_FallsBackToAnIssueUrlInProse()
    {
        // The MCP result is not the clean JSON object (human-readable line, or a shape we do not model): scan for
        // a YouTrack issue URL, which gives both the id and the host.
        var target = YouTrackToolResultParser.TryParse("Created the issue: https://yt.example.com/youtrack/issue/AC-42 — done.");

        target.Should().NotBeNull();
        target!.IssueId.Should().Be("AC-42");
        target.Host.Should().Be("yt.example.com");
    }

    [Fact]
    public void TryParse_FallsBackForANonObjectResultCarryingAnIssueUrl()
    {
        var target = YouTrackToolResultParser.TryParse("""["https://yt.example.com/issue/AC-7"]""");

        target.Should().NotBeNull();
        target!.IssueId.Should().Be("AC-7");
        target.Host.Should().Be("yt.example.com");
    }

    [Fact]
    public void Resolve_MatchesTheInstanceByTheIssueHost()
    {
        var instances = new List<YouTrackInstance>
        {
            new("A", "https://a.example.com/api", "t", ""),
            new("B", "https://b.example.com/api", "t", ""),
        };

        YouTrackInstanceResolver.Resolve(instances, "b.example.com")!.Label.Should().Be("B");
    }

    [Fact]
    public void Resolve_ReturnsNullWhenAKnownHostMatchesNone()
    {
        var instances = new List<YouTrackInstance> { new("A", "https://a.example.com/api", "t", "") };

        // The issue names a different YouTrack than the one configured — attaching to A would be the wrong place.
        YouTrackInstanceResolver.Resolve(instances, "other.example.com").Should().BeNull();
    }

    [Fact]
    public void Resolve_UsesTheSoleInstanceWhenNoHostIsKnown()
    {
        var instances = new List<YouTrackInstance> { new("A", "https://a.example.com/api", "t", "") };

        YouTrackInstanceResolver.Resolve(instances, host: null)!.Label.Should().Be("A");
    }

    [Fact]
    public void Resolve_ReturnsNullWithSeveralInstancesAndNoHost()
    {
        var instances = new List<YouTrackInstance>
        {
            new("A", "https://a.example.com/api", "t", ""),
            new("B", "https://b.example.com/api", "t", ""),
        };

        YouTrackInstanceResolver.Resolve(instances, host: null).Should().BeNull();
    }

    [Fact]
    public void Resolve_IgnoresInstancesMissingUrlOrToken()
    {
        var instances = new List<YouTrackInstance>
        {
            new("Blank", "", "", ""),
            new("Real", "https://a.example.com/api", "t", ""),
        };

        // One real instance among blanks resolves as the sole configured one.
        YouTrackInstanceResolver.Resolve(instances, host: null)!.Label.Should().Be("Real");
    }

    [Fact]
    public void AutoAttachImages_DefaultsOn()
    {
        new YouTrackSettings(new InMemoryPluginStorage()).AutoAttachImages.Should().BeTrue();
    }

    [Fact]
    public void AutoAttachImages_RoundTrips()
    {
        var settings = new YouTrackSettings(new InMemoryPluginStorage()) { AutoAttachImages = false };

        settings.AutoAttachImages.Should().BeFalse();
    }

    // ── The attacher end-to-end, with the upload observed rather than performed ──

    [Fact]
    public async Task HandleAsync_AttachesTheTurnsImagesToTheCreatedIssue()
    {
        var (attacher, host, uploads) = _Attacher();
        var images = _Images();
        host.Observer.ImagesByPane["pane-1"] = images;

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9","url":"https://yt.example.com/x"}"""));

        uploads.Should().ContainSingle();
        uploads[0].Instance.Label.Should().Be("Personal");
        uploads[0].IssueId.Should().Be("AC-9");
        uploads[0].Images.Should().BeSameAs(images);
    }

    [Fact]
    public async Task HandleAsync_AttachesOnAnUpdateToo()
    {
        var (attacher, host, uploads) = _Attacher();
        host.Observer.ImagesByPane["pane-1"] = _Images();

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__update_issue", """{"issueId":"AC-9"}"""));

        uploads.Should().ContainSingle();
        uploads[0].IssueId.Should().Be("AC-9");
    }

    [Fact]
    public async Task HandleAsync_AttachesEachTurnsImagesToAnIssueOnlyOnce()
    {
        var (attacher, host, uploads) = _Attacher();
        host.Observer.ImagesByPane["pane-1"] = _Images();

        // A create and an update to the same issue in the same turn (same image-set instance): attach once.
        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9"}"""));
        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__update_issue", """{"issueId":"AC-9"}"""));

        uploads.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_AttachesAgainForANewTurnsImages()
    {
        var (attacher, host, uploads) = _Attacher();

        host.Observer.ImagesByPane["pane-1"] = _Images();
        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__update_issue", """{"issueId":"AC-9"}"""));

        // A later turn to the same issue carries a fresh image-set → attach again.
        host.Observer.ImagesByPane["pane-1"] = _Images();
        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__update_issue", """{"issueId":"AC-9"}"""));

        uploads.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleAsync_DoesNothingWhenAutoAttachIsOff()
    {
        var (attacher, host, uploads) = _Attacher(autoAttach: false);
        host.Observer.ImagesByPane["pane-1"] = _Images();

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9"}"""));

        uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_DoesNothingWhenTheTurnCarriedNoImages()
    {
        var (attacher, host, uploads) = _Attacher();

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9"}"""));

        uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_IgnoresAnErroredToolCall()
    {
        var (attacher, host, uploads) = _Attacher();
        host.Observer.ImagesByPane["pane-1"] = _Images();

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9"}""", isError: true));

        uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_IgnoresANonYouTrackTool()
    {
        var (attacher, host, uploads) = _Attacher();
        host.Observer.ImagesByPane["pane-1"] = _Images();

        await attacher.HandleAsync(_Activity("pane-1", "Bash", """{"issueId":"AC-9"}"""));

        uploads.Should().BeEmpty();
    }

    private static SessionToolActivity _Activity(string paneId, string toolName, string result, bool isError = false) =>
        new(paneId, toolName, "{}", result, isError);

    private static IReadOnlyList<SessionImageAttachment> _Images() =>
        new List<SessionImageAttachment> { new("image/png", "QUJD", "pasted-image-1.png") };

    private static (YouTrackAutoAttacher Attacher, FakeCockpitHost Host, List<(YouTrackInstance Instance, string IssueId, IReadOnlyList<SessionImageAttachment> Images)> Uploads) _Attacher(bool autoAttach = true)
    {
        var host = new FakeCockpitHost();
        var settings = new YouTrackSettings(new InMemoryPluginStorage())
        {
            Instances = [new("Personal", "https://yt.example.com/api", "token", "AC")],
            AutoAttachImages = autoAttach,
        };

        var uploads = new List<(YouTrackInstance, string, IReadOnlyList<SessionImageAttachment>)>();
        var attacher = new YouTrackAutoAttacher(host, settings, (instance, issueId, images, _) =>
        {
            uploads.Add((instance, issueId, images));
            return Task.FromResult(new AttachOutcome(images.Count, []));
        });

        return (attacher, host, uploads);
    }
}
