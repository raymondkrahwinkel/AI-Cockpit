using FluentAssertions;

namespace Cockpit.Plugin.TranscriptSearch.Tests;

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

    [Fact]
    public async Task SearchAsync_MissingRoot_SearchesTheRemainingOnes()
    {
        _WriteSession("proj", "s", """{"type":"user","message":{"role":"user","content":"the login bug"}}""");

        var service = new TranscriptSearchService([Path.Combine(_root, "does-not-exist"), _root]);

        (await service.SearchAsync("login")).Should().HaveCount(1);
    }

    // A project folder the operator cannot read must not take the whole search down with it: the walk skips it and
    // still returns the hits from every readable transcript. Unix-only — Windows has no chmod equivalent here, and
    // running elevated bypasses the permission entirely, so the test would prove nothing.
    [Fact]
    public async Task SearchAsync_UnreadableProjectFolder_StillReturnsHitsFromTheReadableOnes()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        _WriteSession("readable", "s", """{"type":"user","message":{"role":"user","content":"the login bug"}}""");
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            var service = new TranscriptSearchService([_root]);

            (await service.SearchAsync("login")).Should().HaveCount(1);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The dialog opens on these, so a session is summarised by what you opened it with — an id and a folder name
    // are not something anyone recognises a conversation by.
    [Fact]
    public async Task RecentAsync_SummarisesEachSessionByItsOpeningPrompt_NewestFirst()
    {
        _WriteSession("proj", "old",
            """{"type":"user","message":{"role":"user","content":"fix the login bug"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done"}]}}""");
        _WriteSession("proj", "new",
            """{"type":"user","message":{"role":"user","content":"add the export button"}}""");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "proj", "old.jsonl"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "proj", "new.jsonl"), new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var recent = await new TranscriptSearchService([_root]).RecentAsync();

        recent.Select(hit => hit.SessionId).Should().Equal("new", "old");
        recent[0].Snippet.Should().Be("add the export button");
    }

    // The whole point of #AC-1: a hit carries the folder the session ran in (its cwd), so resuming it can start
    // in the right place rather than wherever the operator last was.
    [Fact]
    public async Task SearchAsync_CapturesTheWorkingDirectoryFromTheTranscriptCwd()
    {
        _WriteSession("encoded-dir-name", "sess1",
            """{"type":"user","cwd":"/home/me/RiderProjects/App","message":{"role":"user","content":"fix the login bug"}}""");

        var hits = await new TranscriptSearchService([_root]).SearchAsync("login");

        hits.Should().ContainSingle()
            .Which.WorkingDirectory.Should().Be("/home/me/RiderProjects/App");
    }

    [Fact]
    public async Task RecentAsync_CarriesTheWorkingDirectory()
    {
        _WriteSession("encoded-dir-name", "sess1",
            """{"type":"user","cwd":"/home/me/RiderProjects/App","message":{"role":"user","content":"fix the login bug"}}""");

        var recent = await new TranscriptSearchService([_root]).RecentAsync();

        recent.Should().ContainSingle()
            .Which.WorkingDirectory.Should().Be("/home/me/RiderProjects/App");
    }

    [Fact]
    public async Task SearchAsync_WithoutACwd_LeavesTheWorkingDirectoryNull()
    {
        _WriteSession("proj", "sess1", """{"type":"user","message":{"role":"user","content":"fix the login bug"}}""");

        var hits = await new TranscriptSearchService([_root]).SearchAsync("login");

        hits.Should().ContainSingle().Which.WorkingDirectory.Should().BeNull();
    }

    [Fact]
    public async Task RecentAsync_ReturnsAtMostTheRequestedNumber()
    {
        for (var i = 0; i < 5; i++)
        {
            _WriteSession("proj", $"s{i}", $$$"""{"type":"user","message":{"role":"user","content":"prompt {{{i}}}"}}""");
        }

        var recent = await new TranscriptSearchService([_root]).RecentAsync(limit: 3);

        recent.Should().HaveCount(3);
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
