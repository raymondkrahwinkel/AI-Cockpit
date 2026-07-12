using Cockpit.Infrastructure.Transcripts;
using FluentAssertions;

namespace Cockpit.Core.Tests.Transcripts;

/// <summary>
/// Searching the on-disk transcripts (#9): matches across files, newest-session-first ordering, the blank-query
/// short-circuit, and skipping unreadable/irrelevant content — exercised against temp JSONL files via the
/// project-roots test seam.
/// </summary>
public class TranscriptSearchServiceTests : IDisposable
{
    private readonly string _root;

    public TranscriptSearchServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"), "projects");
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task SearchAsync_BlankQuery_ReturnsNothing()
    {
        var service = new TranscriptSearchService([_root]);

        (await service.SearchAsync("   ")).Should().BeEmpty();
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

        hits.Should().HaveCount(2);
        hits.Select(hit => hit.Role).Should().Contain(["user", "assistant"]);
        hits.Should().OnlyContain(hit => hit.SessionId == "sess1" && hit.Project == "proj-a");
    }

    [Fact]
    public async Task SearchAsync_OrdersNewestSessionFirst()
    {
        _WriteSession("proj-old", "old", """{"type":"user","message":{"role":"user","content":"shared keyword here"}}""");
        _WriteSession("proj-new", "new", """{"type":"user","message":{"role":"user","content":"shared keyword too"}}""");

        // Make "new" the more-recently-modified transcript.
        File.SetLastWriteTimeUtc(Path.Combine(_root, "proj-old", "old.jsonl"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "proj-new", "new.jsonl"), new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var service = new TranscriptSearchService([_root]);
        var hits = await service.SearchAsync("keyword");

        hits.Should().HaveCount(2);
        hits[0].SessionId.Should().Be("new");
        hits[1].SessionId.Should().Be("old");
    }

    [Fact]
    public async Task SearchAsync_IgnoresNonProseAndUnmatchedLines()
    {
        _WriteSession("proj", "s",
            """{"type":"summary","summary":"login summary"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"login output"}]}}""");

        var service = new TranscriptSearchService([_root]);

        (await service.SearchAsync("login")).Should().BeEmpty();
    }

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
