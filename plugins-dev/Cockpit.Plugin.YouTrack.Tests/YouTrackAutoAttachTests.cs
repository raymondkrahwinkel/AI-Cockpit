
namespace Cockpit.Plugin.YouTrack.Tests;

// The AC-116 automatic image-attach: which tool calls trigger it, what issue it reads from a result, which instance it resolves, and that a turn's images are attached once per issue.
public class YouTrackAutoAttachTests
{
    // A clean create result, and the fallback when the MCP result is prose (human-readable line, or a shape we do
    // not model): scan for a YouTrack issue URL, which gives both the id and the host.
    [Theory]
    [InlineData("""{"issueId":"AC-9","url":"https://yt.example.com/youtrack/issue/AC-9"}""", "AC-9")]
    [InlineData("Created the issue: https://yt.example.com/youtrack/issue/AC-42 — done.", "AC-42")]
    public void TryParse_ReadsIssueIdAndHost(string result, string expectedId)
    {
        var target = YouTrackToolResultParser.TryParse(result);

        Assert.NotNull(target);
        Assert.Equal(expectedId, target!.IssueId);
        Assert.Equal("yt.example.com", target.Host);
    }

    // Matches by the issue host; a host that matches none (attaching to A would be the wrong place) and several
    // instances without a host resolve to nothing; blank instances (no URL or token) do not count, so one real
    // instance among blanks resolves as the sole configured one.
    [Theory]
    [InlineData("A,B", "b.example.com", "B")]
    [InlineData("A", "other.example.com", null)]
    [InlineData("A,B", null, null)]
    [InlineData("Blank,A", null, "A")]
    public void Resolve_PicksTheInstanceForTheIssueHost(string configured, string? host, string? expected)
    {
        var instances = configured.Split(',')
            .Select(label => label == "Blank"
                ? new YouTrackInstance(label, "", "", "")
                : new YouTrackInstance(label, $"https://{label.ToLowerInvariant()}.example.com/api", "t", ""))
            .ToList();

        Assert.Equal(expected, YouTrackInstanceResolver.Resolve(instances, host)?.Label);
    }

    // ── The attacher end-to-end, with the upload observed rather than performed ──
}
