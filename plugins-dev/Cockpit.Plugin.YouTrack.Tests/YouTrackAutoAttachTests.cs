using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

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
        Assert.Equal(expected, YouTrackToolActivity.IsIssueCreateOrUpdate(toolName));
    }

    [Fact]
    public void TryParse_ReadsIssueIdAndHostFromACreateResult()
    {
        var target = YouTrackToolResultParser.TryParse("""{"issueId":"AC-9","url":"https://yt.example.com/youtrack/issue/AC-9"}""");

        Assert.NotNull(target);
        Assert.Equal("AC-9", target!.IssueId);
        Assert.Equal("yt.example.com", target.Host);
    }

    [Fact]
    public void TryParse_ReadsIssueIdWithNoHostFromAnUpdateResult()
    {
        var target = YouTrackToolResultParser.TryParse("""{"issueId":"AC-9","updatedFields":["Stage"]}""");

        Assert.NotNull(target);
        Assert.Equal("AC-9", target!.IssueId);
        Assert.Null(target.Host);
    }

    [Theory]
    [InlineData("""{"idReadable":"AC-9"}""", "AC-9")]
    [InlineData("""{"id":"3-22"}""", "3-22")]
    public void TryParse_FallsBackAcrossIdFieldNames(string json, string expectedId)
    {
        Assert.Equal(expectedId, YouTrackToolResultParser.TryParse(json)!.IssueId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"updatedFields":["Stage"]}""")] // no id, no url
    [InlineData("")]
    public void TryParse_ReturnsNullWhenThereIsNoIssue(string content)
    {
        Assert.Null(YouTrackToolResultParser.TryParse(content));
    }

    [Fact]
    public void TryParse_FallsBackToAnIssueUrlInProse()
    {
        // The MCP result is not the clean JSON object (human-readable line, or a shape we do not model): scan for
        // a YouTrack issue URL, which gives both the id and the host.
        var target = YouTrackToolResultParser.TryParse("Created the issue: https://yt.example.com/youtrack/issue/AC-42 — done.");

        Assert.NotNull(target);
        Assert.Equal("AC-42", target!.IssueId);
        Assert.Equal("yt.example.com", target.Host);
    }

    [Fact]
    public void TryParse_FallsBackForANonObjectResultCarryingAnIssueUrl()
    {
        var target = YouTrackToolResultParser.TryParse("""["https://yt.example.com/issue/AC-7"]""");

        Assert.NotNull(target);
        Assert.Equal("AC-7", target!.IssueId);
        Assert.Equal("yt.example.com", target.Host);
    }

    [Fact]
    public void Resolve_MatchesTheInstanceByTheIssueHost()
    {
        var instances = new List<YouTrackInstance>
        {
            new("A", "https://a.example.com/api", "t", ""),
            new("B", "https://b.example.com/api", "t", ""),
        };

        Assert.Equal("B", YouTrackInstanceResolver.Resolve(instances, "b.example.com")!.Label);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenAKnownHostMatchesNone()
    {
        var instances = new List<YouTrackInstance> { new("A", "https://a.example.com/api", "t", "") };

        // The issue names a different YouTrack than the one configured — attaching to A would be the wrong place.
        Assert.Null(YouTrackInstanceResolver.Resolve(instances, "other.example.com"));
    }

    [Fact]
    public void Resolve_UsesTheSoleInstanceWhenNoHostIsKnown()
    {
        var instances = new List<YouTrackInstance> { new("A", "https://a.example.com/api", "t", "") };

        Assert.Equal("A", YouTrackInstanceResolver.Resolve(instances, host: null)!.Label);
    }

    [Fact]
    public void Resolve_ReturnsNullWithSeveralInstancesAndNoHost()
    {
        var instances = new List<YouTrackInstance>
        {
            new("A", "https://a.example.com/api", "t", ""),
            new("B", "https://b.example.com/api", "t", ""),
        };

        Assert.Null(YouTrackInstanceResolver.Resolve(instances, host: null));
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
        Assert.Equal("Real", YouTrackInstanceResolver.Resolve(instances, host: null)!.Label);
    }

    [Fact]
    public void AutoAttachImages_DefaultsOn()
    {
        Assert.True(new YouTrackSettings(new InMemoryPluginStorage()).AutoAttachImages);
    }

    [Fact]
    public void AutoAttachImages_RoundTrips()
    {
        var settings = new YouTrackSettings(new InMemoryPluginStorage()) { AutoAttachImages = false };

        Assert.False(settings.AutoAttachImages);
    }

    // ── The attacher end-to-end, with the upload observed rather than performed ──

    [Fact]
    public async Task HandleAsync_AttachesTheTurnsImagesToTheCreatedIssue()
    {
        var (attacher, host, uploads) = _Attacher();
        var images = _Images();
        host.Observer.ImagesByPane["pane-1"] = images;

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9","url":"https://yt.example.com/x"}"""));

        Assert.Single(uploads);
        Assert.Equal("Personal", uploads[0].Instance.Label);
        Assert.Equal("AC-9", uploads[0].IssueId);
        Assert.Same(images, uploads[0].Images);
    }

    [Fact]
    public async Task HandleAsync_AttachesOnAnUpdateToo()
    {
        var (attacher, host, uploads) = _Attacher();
        host.Observer.ImagesByPane["pane-1"] = _Images();

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__update_issue", """{"issueId":"AC-9"}"""));

        Assert.Single(uploads);
        Assert.Equal("AC-9", uploads[0].IssueId);
    }

    [Fact]
    public async Task HandleAsync_AttachesEachTurnsImagesToAnIssueOnlyOnce()
    {
        var (attacher, host, uploads) = _Attacher();
        host.Observer.ImagesByPane["pane-1"] = _Images();

        // A create and an update to the same issue in the same turn (same image-set instance): attach once.
        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9"}"""));
        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__update_issue", """{"issueId":"AC-9"}"""));

        Assert.Single(uploads);
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

        Assert.Equal(2, System.Linq.Enumerable.Count(uploads));
    }

    [Fact]
    public async Task HandleAsync_DoesNothingWhenAutoAttachIsOff()
    {
        var (attacher, host, uploads) = _Attacher(autoAttach: false);
        host.Observer.ImagesByPane["pane-1"] = _Images();

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9"}"""));

        Assert.Empty(uploads);
    }

    [Fact]
    public async Task HandleAsync_DoesNothingWhenTheTurnCarriedNoImages()
    {
        var (attacher, host, uploads) = _Attacher();

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9"}"""));

        Assert.Empty(uploads);
    }

    [Fact]
    public async Task HandleAsync_IgnoresAnErroredToolCall()
    {
        var (attacher, host, uploads) = _Attacher();
        host.Observer.ImagesByPane["pane-1"] = _Images();

        await attacher.HandleAsync(_Activity("pane-1", "mcp__youtrack__create_issue", """{"issueId":"AC-9"}""", isError: true));

        Assert.Empty(uploads);
    }

    [Fact]
    public async Task HandleAsync_IgnoresANonYouTrackTool()
    {
        var (attacher, host, uploads) = _Attacher();
        host.Observer.ImagesByPane["pane-1"] = _Images();

        await attacher.HandleAsync(_Activity("pane-1", "Bash", """{"issueId":"AC-9"}"""));

        Assert.Empty(uploads);
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
