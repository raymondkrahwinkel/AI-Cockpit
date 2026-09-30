
namespace Cockpit.Plugin.YouTrack.Tests;

// The AC-116 automatic image-attach: which tool calls trigger it, what issue it reads from a result, which instance it resolves, and that a turn's images are attached once per issue.
public class YouTrackAutoAttachTests
{
    [Fact]
    public void TryParse_ReadsIssueIdAndHostFromACreateResult()
    {
        var target = YouTrackToolResultParser.TryParse("""{"issueId":"AC-9","url":"https://yt.example.com/youtrack/issue/AC-9"}""");

        Assert.NotNull(target);
        Assert.Equal("AC-9", target!.IssueId);
        Assert.Equal("yt.example.com", target.Host);
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

    // ── The attacher end-to-end, with the upload observed rather than performed ──
}
