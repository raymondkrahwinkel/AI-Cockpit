
namespace Cockpit.Plugin.TranscriptSearch.Tests;

// Searching the on-disk transcripts (#9): matches across files, newest-session-first ordering, the blank-query
// short-circuit, and skipping unreadable/irrelevant content — exercised against temp JSONL files via the
// project-roots test seam.
public class TranscriptSearchServiceTests : IDisposable
{
    private readonly string _root;

    public TranscriptSearchServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"), "projects");
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task SearchAsync_FindsMatchingUserAndAssistantLines()
    {
        _WriteSession("proj-a", "sess1",
            """{"type":"user","message":{"role":"user","content":"please fix the login bug"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the login flow"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"unrelated reply"}]}}""");

        var service = new TranscriptSearchService([_root]);
        var hits = await service.SearchAsync("login");

        Assert.Equal(2, System.Linq.Enumerable.Count(hits));
        var roles = hits.Select(hit => hit.Role).ToList();
        Assert.Contains("user", roles);
        Assert.Contains("assistant", roles);
        Assert.All(hits, hit => Assert.True(hit.SessionId == "sess1" && hit.Project == "proj-a"));
    }

    // A project folder the operator cannot read must not take the whole search down with it: the walk skips it and
    // still returns the hits from every readable transcript. Unix-only — Windows has no chmod equivalent here, and
    // running elevated bypasses the permission entirely, so the test would prove nothing.

    // The dialog opens on these, so a session is summarised by what you opened it with — an id and a folder name
    // are not something anyone recognises a conversation by.

    // The whole point of #AC-1: a hit carries the folder the session ran in (its cwd), so resuming it can start
    // in the right place rather than wherever the operator last was.

    private void _WriteSession(string project, string sessionId, params string[] lines)
    {
        var dir = Path.Combine(_root, project);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, $"{sessionId}.jsonl"), lines);
    }

    public void Dispose()
    {
        var sessionDir = Directory.GetParent(_root)?.FullName;
        if (sessionDir is not null && Directory.Exists(sessionDir))
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }
}
