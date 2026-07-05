using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Zyra.Voice.Core.Claude;
using Zyra.Voice.Infrastructure.Claude;

namespace Zyra.Voice.Core.Tests.Claude;

/// <summary>
/// Exercises <see cref="ClaudeCliSession"/>'s turn-taking/event-mapping logic against a fake
/// <see cref="IClaudeCliProcess"/> — no real <c>claude</c> process is spawned. A live
/// end-to-end run against the actual CLI requires Raymond's logged-in environment; that is
/// explicitly out of scope for this sandbox (no logged-in <c>claude</c> is available here).
/// </summary>
public class ClaudeCliSessionTests
{
    [Fact]
    public async Task StartAsync_StartsUnderlyingProcess()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, NullLogger<ClaudeCliSession>.Instance);

        await session.StartAsync();

        process.Started.Should().BeTrue();
    }

    [Fact]
    public async Task SendUserMessageAsync_WritesStreamJsonUserMessageLine()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.SendUserMessageAsync("hello there");

        process.WrittenLines.Should().ContainSingle();
        var written = process.WrittenLines[0];
        written.Should().Contain("\"type\":\"user\"");
        written.Should().Contain("\"role\":\"user\"");
        written.Should().Contain("hello there");
    }

    [Fact]
    public async Task Events_SystemInitLine_YieldsSessionInitializedAndSetsSessionId()
    {
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"system","subtype":"init","session_id":"sess-abc","model":"claude-opus-4-6","tools":["Read"]}""");
        process.CompleteOutput();

        await using var session = new ClaudeCliSession(process, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var events = await CollectEventsAsync(session);

        events.Should().ContainSingle().Which.Should().BeOfType<SessionInitialized>();
        session.SessionId.Should().Be("sess-abc");
    }

    [Fact]
    public async Task Events_AssistantTextThenToolUse_YieldsTextThenToolUseThenPermissionRequested()
    {
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""
            {"type":"assistant","session_id":"sess-1","message":{"role":"assistant","content":[{"type":"text","text":"Checking the file."},{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"a.txt"}}]}}
            """);
        process.CompleteOutput();

        await using var session = new ClaudeCliSession(process, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var events = await CollectEventsAsync(session);

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<AssistantTextCompleted>().Which.Text.Should().Be("Checking the file.");
        events[1].Should().BeOfType<ToolUseRequested>().Which.ToolName.Should().Be("Read");
        var permission = events[2].Should().BeOfType<PermissionRequested>().Subject;
        permission.ToolUseId.Should().Be("toolu_1");
        permission.ToolName.Should().Be("Read");
    }

    [Fact]
    public async Task Events_ResultLine_YieldsTurnCompleted()
    {
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"result","subtype":"success","is_error":false,"result":"All done.","session_id":"sess-1"}""");
        process.CompleteOutput();

        await using var session = new ClaudeCliSession(process, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var events = await CollectEventsAsync(session);

        var turn = events.Should().ContainSingle().Subject.Should().BeOfType<TurnCompleted>().Subject;
        turn.Result.Should().Be("All done.");
        turn.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Events_MalformedLine_YieldsSessionError()
    {
        var process = new FakeClaudeCliProcess();
        process.Enqueue("{not valid json");
        process.CompleteOutput();

        await using var session = new ClaudeCliSession(process, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var events = await CollectEventsAsync(session);

        events.Should().ContainSingle().Which.Should().BeOfType<SessionError>();
    }

    [Fact]
    public async Task RespondToPermissionAsync_DoesNotThrow_AndCompletes()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var act = async () => await session.RespondToPermissionAsync("toolu_1", allow: true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_DisposesUnderlyingProcess()
    {
        var process = Substitute.For<IClaudeCliProcess>();
        process.ReadLinesAsync(Arg.Any<CancellationToken>()).Returns(EmptyAsync());
        var session = new ClaudeCliSession(process, NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.DisposeAsync();

        await process.Received(1).DisposeAsync();
    }

    private static async IAsyncEnumerable<string> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task<List<ClaudeSessionEvent>> CollectEventsAsync(Zyra.Voice.Core.Abstractions.Claude.IClaudeSession session)
    {
        var events = new List<ClaudeSessionEvent>();
        await foreach (var evt in session.Events)
        {
            events.Add(evt);
        }

        return events;
    }
}
