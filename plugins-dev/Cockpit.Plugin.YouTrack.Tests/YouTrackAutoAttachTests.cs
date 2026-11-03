
namespace Cockpit.Plugin.YouTrack.Tests;

// The AC-116 automatic image-attach: which tool calls trigger it, what issue it reads from a result, which instance it resolves, and that a turn's images are attached once per issue.
public class YouTrackAutoAttachTests
{

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

    // ── The attacher end-to-end, with the upload observed rather than performed ──
}
