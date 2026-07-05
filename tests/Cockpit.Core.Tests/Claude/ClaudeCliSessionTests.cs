using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Claude;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Claude;

namespace Cockpit.Core.Tests.Claude;

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
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);

        await session.StartAsync();

        process.Started.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_WithProfile_PassesProfileToProcessAndExposesItOnSession()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        var profile = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");

        await session.StartAsync(profile);

        process.StartedWithProfile.Should().Be(profile);
        session.Profile.Should().Be(profile);
    }

    [Fact]
    public async Task StartAsync_WithoutProfile_LeavesProfileNull()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);

        await session.StartAsync();

        process.StartedWithProfile.Should().BeNull();
        session.Profile.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_WithModel_PassesModelToProcess()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);

        await session.StartAsync(model: "opus");

        process.StartedWithModel.Should().Be("opus");
    }

    [Fact]
    public async Task StartAsync_WithoutModel_LeavesStartedWithModelNull()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);

        await session.StartAsync();

        process.StartedWithModel.Should().BeNull();
    }

    [Fact]
    public async Task SetPermissionModeAsync_WritesControlRequestWithSubtypeAndMode()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.SetPermissionModeAsync("plan");

        process.WrittenLines.Should().ContainSingle();
        var written = process.WrittenLines[0];
        written.Should().Contain("\"type\":\"control_request\"");
        written.Should().Contain("\"subtype\":\"set_permission_mode\"");
        written.Should().Contain("\"mode\":\"plan\"");
        written.Should().Contain("\"request_id\":");
    }

    [Fact]
    public async Task SetModelAsync_WritesControlRequestWithSubtypeAndModel()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.SetModelAsync("haiku");

        process.WrittenLines.Should().ContainSingle();
        var written = process.WrittenLines[0];
        written.Should().Contain("\"type\":\"control_request\"");
        written.Should().Contain("\"subtype\":\"set_model\"");
        written.Should().Contain("\"model\":\"haiku\"");
        written.Should().Contain("\"request_id\":");
    }

    [Fact]
    public async Task SetMaxThinkingTokensAsync_WritesControlRequestWithSubtypeAndBudget()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.SetMaxThinkingTokensAsync(24_000);

        process.WrittenLines.Should().ContainSingle();
        var written = process.WrittenLines[0];
        written.Should().Contain("\"type\":\"control_request\"");
        written.Should().Contain("\"subtype\":\"set_max_thinking_tokens\"");
        written.Should().Contain("\"maxThinkingTokens\":24000");
        written.Should().Contain("\"request_id\":");
    }

    [Fact]
    public async Task InterruptAsync_WritesControlRequestWithInterruptSubtype()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.InterruptAsync();

        process.WrittenLines.Should().ContainSingle();
        var written = process.WrittenLines[0];
        written.Should().Contain("\"type\":\"control_request\"");
        written.Should().Contain("\"subtype\":\"interrupt\"");
        written.Should().Contain("\"request_id\":");
    }

    [Fact]
    public async Task SetPermissionModeAsync_EachCall_UsesAFreshRequestId()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.SetPermissionModeAsync("plan");
        await session.SetPermissionModeAsync("auto");

        process.WrittenLines.Should().HaveCount(2);
        process.WrittenLines[0].Should().NotBe(process.WrittenLines[1]);
    }

    [Fact]
    public async Task Events_ControlResponseLine_DoesNotYieldAnEventAndDoesNotThrow()
    {
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"control_response","response":{"subtype":"success","request_id":"abc"}}""");
        process.Enqueue("""{"type":"result","subtype":"success","is_error":false,"result":"still alive","session_id":"S1"}""");
        process.CompleteOutput();

        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var events = await CollectEventsAsync(session);

        events.Should().ContainSingle().Which.Should().BeOfType<TurnCompleted>();
    }

    [Fact]
    public async Task SendUserMessageAsync_WritesStreamJsonUserMessageLine()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.SendUserMessageAsync("hello there");

        process.WrittenLines.Should().ContainSingle();
        var written = process.WrittenLines[0];
        written.Should().Contain("\"type\":\"user\"");
        written.Should().Contain("\"role\":\"user\"");
        written.Should().Contain("hello there");
    }

    [Fact]
    public async Task SendUserMessageAsync_TextOnly_UsesPlainStringContent()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.SendUserMessageAsync("just text");

        using var document = JsonDocument.Parse(process.WrittenLines.Should().ContainSingle().Subject);
        var content = document.RootElement.GetProperty("message").GetProperty("content");
        content.ValueKind.Should().Be(JsonValueKind.String);
        content.GetString().Should().Be("just text");
    }

    [Fact]
    public async Task SendUserMessageAsync_EmptyImageList_UsesPlainStringContent()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.SendUserMessageAsync("no images", images: []);

        using var document = JsonDocument.Parse(process.WrittenLines.Should().ContainSingle().Subject);
        document.RootElement.GetProperty("message").GetProperty("content").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task SendUserMessageAsync_WithImage_UsesContentBlockArrayWithTextThenImage()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var image = ImageAttachment.FromBytes([1, 2, 3, 4], "image/png");
        await session.SendUserMessageAsync("look at this", [image]);

        using var document = JsonDocument.Parse(process.WrittenLines.Should().ContainSingle().Subject);
        var content = document.RootElement.GetProperty("message").GetProperty("content");
        content.ValueKind.Should().Be(JsonValueKind.Array);
        content.GetArrayLength().Should().Be(2);

        var textBlock = content[0];
        textBlock.GetProperty("type").GetString().Should().Be("text");
        textBlock.GetProperty("text").GetString().Should().Be("look at this");

        var imageBlock = content[1];
        imageBlock.GetProperty("type").GetString().Should().Be("image");
        var source = imageBlock.GetProperty("source");
        source.GetProperty("type").GetString().Should().Be("base64");
        source.GetProperty("media_type").GetString().Should().Be("image/png");
        source.GetProperty("data").GetString().Should().Be(Convert.ToBase64String([1, 2, 3, 4]));
    }

    [Fact]
    public async Task SendUserMessageAsync_WithMultipleImages_EmitsOneImageBlockPerAttachment()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        ImageAttachment[] images =
        [
            ImageAttachment.FromBytes([10], "image/png"),
            ImageAttachment.FromBytes([20], "image/jpeg"),
        ];
        await session.SendUserMessageAsync("two", images);

        using var document = JsonDocument.Parse(process.WrittenLines.Should().ContainSingle().Subject);
        var content = document.RootElement.GetProperty("message").GetProperty("content");
        content.GetArrayLength().Should().Be(3);
        content[1].GetProperty("source").GetProperty("media_type").GetString().Should().Be("image/png");
        content[2].GetProperty("source").GetProperty("media_type").GetString().Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Events_SystemInitLine_YieldsSessionInitializedAndSetsSessionId()
    {
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"system","subtype":"init","session_id":"sess-abc","model":"claude-opus-4-6","tools":["Read"]}""");
        process.CompleteOutput();

        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
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

        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
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

        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
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

        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var events = await CollectEventsAsync(session);

        events.Should().ContainSingle().Which.Should().BeOfType<SessionError>();
    }

    [Fact]
    public async Task Events_FullCapturedTurn_YieldsThinkingSeparateFromTextThenStatusThenRateLimitThenTurnCompleted()
    {
        // Verbatim capture from a live claude.exe v2.1.197 stream-json turn — see
        // Memory/Zyra-Voice/StreamJson-Schema.md. Session/message ids shortened.
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"system","subtype":"init","cwd":"C:\\Users\\raymo","session_id":"S1","tools":["Task","Bash","Read"]}""");
        process.Enqueue("""{"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-4-8","id":"msg_1","type":"message","role":"assistant","content":[],"stop_reason":null,"usage":{"input_tokens":2949}}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u1"}""");
        process.Enqueue("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u2"}""");
        process.Enqueue("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"hallo Raymond"}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u3"}""");
        process.Enqueue("""{"type":"assistant","message":{"model":"claude-opus-4-8","id":"msg_1","role":"assistant","content":[{"type":"text","text":"hallo Raymond"}],"stop_reason":null,"usage":{"input_tokens":2949}}}""");
        process.Enqueue("""{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":128}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u4"}""");
        process.Enqueue("""{"type":"system","subtype":"post_turn_summary","summarizes_uuid":"x","status_category":"review_ready","status_detail":"done","needs_action":"","uuid":"u5","session_id":"S1"}""");
        process.Enqueue("""{"type":"system","subtype":"notification","key":"stop-hook-error","text":"Stop hook error occurred","priority":"immediate","uuid":"u6","session_id":"S1"}""");
        process.Enqueue("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1783260000,"rateLimitType":"five_hour","overageStatus":"rejected","isUsingOverage":false},"uuid":"u7","session_id":"S1"}""");
        process.Enqueue("""{"type":"result","subtype":"success","is_error":false,"result":"hallo Raymond","stop_reason":"end_turn","session_id":"S1","num_turns":1,"terminal_reason":"completed"}""");
        process.CompleteOutput();

        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var events = await CollectEventsAsync(session);

        // message_start/content_block_start/message_delta carry no cockpit-visible payload today.
        events.Should().HaveCount(7);
        events[0].Should().BeOfType<SessionInitialized>();

        events[1].Should().BeOfType<AssistantTextDelta>().Which.Text.Should().Be("hallo Raymond");
        events.OfType<AssistantThinkingDelta>().Should().BeEmpty("the fixture's thinking block only had a block-start, no thinking_delta");

        events[2].Should().BeOfType<AssistantTextCompleted>().Which.Text.Should().Be("hallo Raymond");

        var reviewReady = events[3].Should().BeOfType<SessionStatusChanged>().Subject;
        reviewReady.StatusCategory.Should().Be("review_ready");
        reviewReady.NotificationText.Should().BeNull();

        var notification = events[4].Should().BeOfType<SessionStatusChanged>().Subject;
        notification.NotificationText.Should().Be("Stop hook error occurred");
        notification.NotificationPriority.Should().Be("immediate");

        var rateLimit = events[5].Should().BeOfType<RateLimitInfo>().Subject;
        rateLimit.RateLimitType.Should().Be("five_hour");

        var turn = events[6].Should().BeOfType<TurnCompleted>().Subject;
        turn.Result.Should().Be("hallo Raymond");
        turn.TerminalReason.Should().Be("completed");

        session.SessionId.Should().Be("S1");
    }

    [Fact]
    public async Task Events_ThinkingDeltaThenTextDelta_YieldsSeparateEventTypesInOrder()
    {
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Weighing the options."}},"session_id":"S1","parent_tool_use_id":null,"uuid":"ta"}""");
        process.Enqueue("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Here is my answer."}},"session_id":"S1","parent_tool_use_id":null,"uuid":"tb"}""");
        process.CompleteOutput();

        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var events = await CollectEventsAsync(session);

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<AssistantThinkingDelta>().Which.Thinking.Should().Be("Weighing the options.");
        events[1].Should().BeOfType<AssistantTextDelta>().Which.Text.Should().Be("Here is my answer.");
    }

    [Fact]
    public async Task Events_UnknownTypeLine_YieldsUnknownEvent_DoesNotCrashThePump()
    {
        var process = new FakeClaudeCliProcess();
        process.Enqueue("""{"type":"some_brand_new_event","session_id":"S1","weird_field":42}""");
        process.Enqueue("""{"type":"result","subtype":"success","is_error":false,"result":"still alive","session_id":"S1"}""");
        process.CompleteOutput();

        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var events = await CollectEventsAsync(session);

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<UnknownEvent>();
        events[1].Should().BeOfType<TurnCompleted>().Which.Result.Should().Be("still alive");
    }

    [Fact]
    public async Task RespondToPermissionAsync_DoesNotThrow_AndCompletes()
    {
        var process = new FakeClaudeCliProcess();
        await using var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        var act = async () => await session.RespondToPermissionAsync("toolu_1", allow: true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_DisposesUnderlyingProcess()
    {
        var process = Substitute.For<IClaudeCliProcess>();
        process.ReadLinesAsync(Arg.Any<CancellationToken>()).Returns(EmptyAsync());
        var session = new ClaudeCliSession(process, new RecordingPermissionCoordinator(), NullLogger<ClaudeCliSession>.Instance);
        await session.StartAsync();

        await session.DisposeAsync();

        await process.Received(1).DisposeAsync();
    }

    private static async IAsyncEnumerable<string> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task<List<ClaudeSessionEvent>> CollectEventsAsync(Cockpit.Core.Abstractions.Claude.IClaudeSession session)
    {
        var events = new List<ClaudeSessionEvent>();
        await foreach (var evt in session.Events)
        {
            events.Add(evt);
        }

        return events;
    }
}
