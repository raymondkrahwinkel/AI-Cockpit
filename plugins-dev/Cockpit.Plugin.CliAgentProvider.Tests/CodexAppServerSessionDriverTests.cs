using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CodexAppServerSessionDriver"/> against a <see cref="FakeCliSubprocess"/> (#45 fase 3) — proves the
/// app-server lifecycle without a live Codex: the initialize/thread/start handshake surfaces a
/// <see cref="PluginSessionInitialized"/> with the thread id, the cwd the cockpit passed rides thread/start
/// (D5), agent-message deltas stream, an approval request is surfaced and answered, and a resume uses the
/// existing thread id.
/// </summary>
public class CodexAppServerSessionDriverTests
{
    private static CliAgentConfig _DefaultConfig() => new(WorkingDirectory: Path.GetTempPath());

    [Fact]
    public async Task Start_DoesHandshake_PassesTheCockpitCwd_AndEmitsSessionInitialized()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var startTask = driver.StartAsync("gpt-5-codex", "/work/here", resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var threadStart = await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        threadStart.GetProperty("params").GetProperty("cwd").GetString().Should().Be("/work/here");
        threadStart.GetProperty("params").GetProperty("model").GetString().Should().Be("gpt-5-codex");
        fake.WrittenLines.Should().Contain(line => line.Contains("\"method\":\"initialized\""));

        var initialized = await _NextEventAsync(driver);
        initialized.Should().BeOfType<PluginSessionInitialized>();
        driver.SessionId.Should().Be("thread-1");
    }

    [Fact]
    public async Task Start_UsesThePerSessionSandboxAndModelOptions_OverConfig_InThreadStart()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        // _DefaultConfig has sandbox "read-only"; the dialog's per-session choice must win.
        var options = new Dictionary<string, string> { ["sandbox"] = "workspace-write", ["model"] = "o3" };
        var startTask = driver.StartAsync(null, "/work", resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var threadStart = await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        threadStart.GetProperty("params").GetProperty("sandbox").GetString().Should().Be("workspace-write");
        threadStart.GetProperty("params").GetProperty("model").GetString().Should().Be("o3");
    }

    [Fact]
    public async Task Start_PassesTheSessionsMcpServers_AsConfigArgs_WithTheTokenInTheEnvironmentNotTheCommandLine()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        const string token = "yt-pat-value";
        PluginMcpServer[] mcpServers =
        [
            new() { Name = "cockpit-orchestrator", Url = "http://127.0.0.1:8765/mcp" },
            new() { Name = "youtrack", Url = "http://127.0.0.1:9000/mcp", BearerToken = token },
        ];

        var startTask = driver.StartAsync(null, "/work", resumeSessionId: null, options: null, mcpServers, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        // The MCP servers ride -c overrides placed before the subcommand, which stays last.
        fake.Arguments.Should().ContainInOrder("-c", """mcp_servers.cockpit-orchestrator={ url = "http://127.0.0.1:8765/mcp" }""");
        fake.Arguments!.Last().Should().Be("app-server");

        // The bearer token is never on the command line (that would leak it in /proc/<pid>/cmdline) — only its
        // env-var name is, and the token itself reaches the child through the process environment.
        fake.Arguments.Should().NotContain(argument => argument.Contains(token));
        fake.EnvironmentVariables.Should().Contain(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_1", token));
    }

    [Fact]
    public async Task Start_WithResume_SendsThreadResume_ForThatThreadId()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var startTask = driver.StartAsync(null, "/work", resumeSessionId: "thread-99", options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var resume = await _RespondAsync(fake, "thread/resume", """{"threadId":"thread-99"}""");
        await startTask;

        resume.GetProperty("params").GetProperty("threadId").GetString().Should().Be("thread-99");
        driver.SessionId.Should().Be("thread-99");
    }

    [Fact]
    public async Task SendUserMessage_StreamsAgentDeltas_ThenCompletesTheTurn()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"turn/started","params":{"threadId":"thread-1","turn":{"id":"turn-1"}}}""");
        await fake.PushStdoutAsync("""{"method":"item/agentMessage/delta","params":{"delta":"Hello, ","itemId":"i1","threadId":"thread-1","turnId":"turn-1"}}""");
        await fake.PushStdoutAsync("""{"method":"item/agentMessage/delta","params":{"delta":"world!","itemId":"i1","threadId":"thread-1","turnId":"turn-1"}}""");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed"}}}""");

        var events = await _CollectUntilTurnCompletedAsync(driver);

        string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text)).Should().Be("Hello, world!");
        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Approval_IsSurfaced_AndAnsweredWithTheDecision()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("run ls");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"id":55,"method":"item/commandExecution/requestApproval","params":{"itemId":"cmd-1","command":"ls -la","threadId":"thread-1","turnId":"turn-1"}}""");

        var permission = await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);
        permission.ToolUseId.Should().Be("cmd-1");
        permission.ToolName.Should().Be("shell");

        await driver.RespondToPermissionAsync("cmd-1", allow: true);

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":55");
        using var document = JsonDocument.Parse(answer);
        document.RootElement.GetProperty("result").GetProperty("decision").GetString().Should().Be("accept");
    }

    [Fact]
    public async Task Approval_Deny_IsAnsweredWithDecline()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("run rm");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"id":57,"method":"item/commandExecution/requestApproval","params":{"itemId":"cmd-2","command":"rm -rf /","threadId":"thread-1","turnId":"turn-1"}}""");
        await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);

        // The decline branch shares _RespondDecisionAsync with accept/acceptForSession — cover it so the refactor stays honest.
        await driver.RespondToPermissionAsync("cmd-2", allow: false);

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":57");
        using var document = JsonDocument.Parse(answer);
        document.RootElement.GetProperty("result").GetProperty("decision").GetString().Should().Be("decline");
    }

    [Fact]
    public async Task Approval_AllowAlways_IsAnsweredWithAcceptForSession()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("run ls");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"id":56,"method":"item/fileChange/requestApproval","params":{"itemId":"edit-1","threadId":"thread-1","turnId":"turn-1"}}""");
        await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);

        // D4: "allow always" is acceptForSession, so the agent stops asking for the like of it this thread.
        await driver.AllowPermissionAlwaysAsync("edit-1");

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":56");
        using var document = JsonDocument.Parse(answer);
        document.RootElement.GetProperty("result").GetProperty("decision").GetString().Should().Be("acceptForSession");
    }

    [Fact]
    public async Task ProcessId_ReflectsTheSpawnedAppServerProcess()
    {
        var fake = new FakeCliSubprocess { ProcessId = 9999 };
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // D10: the resource meter measures the codex app-server process this session runs in.
        driver.ProcessId.Should().Be(9999);
    }

    [Fact]
    public async Task TurnCompleted_WithInterruptedStatus_IsNotReportedAsError()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"interrupted"}}}""");

        var events = await _CollectUntilTurnCompletedAsync(driver);
        var completed = events.OfType<PluginTurnCompleted>().Should().ContainSingle().Subject;
        completed.IsError.Should().BeFalse();
        completed.StopReason.Should().Be("interrupt");
    }

    [Fact]
    public async Task UnmodeledServerRequest_IsAnsweredWithAJsonRpcError_NotAMalformedDecision()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // item/permissions/requestApproval expects { permissions }, not { decision } — answering with a decision
        // would be a malformed response. The driver must reply with a JSON-RPC error instead (increment 1).
        await fake.PushStdoutAsync("""{"id":88,"method":"item/permissions/requestApproval","params":{"itemId":"p-1","threadId":"thread-1","turnId":"turn-1"}}""");

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":88");
        using var document = JsonDocument.Parse(answer);
        document.RootElement.TryGetProperty("error", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("result", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TokenUsageNotification_FillsTheContextPercent_FromTheLastTurnOverTheModelWindow()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // D7: how full the context window is = the last turn's footprint over the model's window (50k / 200k = 25%).
        await fake.PushStdoutAsync("""{"method":"thread/tokenUsage/updated","params":{"threadId":"thread-1","turnId":"turn-1","tokenUsage":{"last":{"inputTokens":40000,"outputTokens":10000,"cachedInputTokens":0,"reasoningOutputTokens":0,"totalTokens":50000},"total":{"inputTokens":100000,"outputTokens":20000,"cachedInputTokens":0,"reasoningOutputTokens":0,"totalTokens":120000},"modelContextWindow":200000}}}""");

        var status = await _WaitForStatusAsync(driver, current => current.ContextUsedPercent is not null);
        status.ContextUsedPercent.Should().Be(25);
    }

    [Fact]
    public async Task Notification_WithoutParams_DoesNotKillThePump()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // A param-less notification reaches the handler as default(JsonElement); the pump must survive it, or one
        // malformed line would tear down the whole session's event stream. The valid update that follows proves
        // the pump lived: without the entry guard the first line throws and the second never gets processed.
        await fake.PushStdoutAsync("""{"method":"account/rateLimits/updated"}""");
        await fake.PushStdoutAsync("""{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"last":{"totalTokens":50000},"modelContextWindow":200000}}}""");

        var status = await _WaitForStatusAsync(driver, current => current.ContextUsedPercent is not null);
        status.ContextUsedPercent.Should().Be(25);
    }

    [Fact]
    public async Task RateLimitsNotification_FillsBothWindows_WithTheirUsedPercentSpanAndReset()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // D7: the account snapshot's windows carry usedPercent, an epoch reset, and a span the driver turns into a
        // label the header shows (300 min → "5h", 10080 min → "7d") — the provider owns the label, not the host.
        await fake.PushStdoutAsync("""{"method":"account/rateLimits/updated","params":{"rateLimits":{"primary":{"usedPercent":60,"resetsAt":1800000000,"windowDurationMins":300},"secondary":{"usedPercent":80,"resetsAt":1800600000,"windowDurationMins":10080}}}}""");

        var status = await _WaitForStatusAsync(driver, current => current.RateLimits.Count > 0);
        status.RateLimits.Should().Equal(
            new PluginRateLimitWindow("5h", 60, DateTimeOffset.FromUnixTimeSeconds(1800000000), 300),
            new PluginRateLimitWindow("7d", 80, DateTimeOffset.FromUnixTimeSeconds(1800600000), 10080));
    }

    [Fact]
    public async Task SessionInitialized_CarriesTheWorkingDirectory()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var startTask = driver.StartAsync(null, "/work/here", resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        // D3: the session reports its cwd so the host's git-status header and active-cwd observer follow it.
        var initialized = await _NextEventOfTypeAsync<PluginSessionInitialized>(driver);
        initialized.Cwd.Should().Be("/work/here");
    }

    [Fact]
    public async Task ReasoningDelta_IsSurfacedAsAThinkingEvent()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("think");
        await _WaitForRequestIdAsync(fake, "turn/start");
        // D3: Codex's reasoning trace becomes a thinking event the host renders dimmed, separate from the answer.
        await fake.PushStdoutAsync("""{"method":"item/reasoning/textDelta","params":{"delta":"Let me consider","itemId":"r1","threadId":"thread-1","turnId":"turn-1"}}""");

        var thinking = await _NextEventOfTypeAsync<PluginAssistantThinkingDelta>(driver);
        thinking.Thinking.Should().Be("Let me consider");
    }

    [Fact]
    public async Task TurnCompleted_CarriesTheLastTurnsTokenUsage_ReasoningFoldedIntoOutput()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"last":{"inputTokens":1000,"outputTokens":200,"cachedInputTokens":50,"reasoningOutputTokens":30,"totalTokens":1280},"modelContextWindow":200000}}}""");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed"}}}""");

        // D3: the turn's usage feeds the host token meter — reasoning output (30) folds into output (200), cached
        // input (50) maps to cache-read, and Codex reports no cache-creation count.
        var events = await _CollectUntilTurnCompletedAsync(driver);
        events.OfType<PluginTurnCompleted>().Should().ContainSingle()
            .Which.Usage.Should().Be(new PluginTokenUsage(1000, 230, 50, 0));
    }

    [Fact]
    public async Task TurnWithoutItsOwnUsage_DoesNotInheritThePreviousTurnsUsage()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // Turn 1 reports usage.
        await driver.SendUserMessageAsync("one");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"turn/started","params":{"threadId":"thread-1","turn":{"id":"turn-1"}}}""");
        await fake.PushStdoutAsync("""{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"last":{"inputTokens":1000,"outputTokens":200,"cachedInputTokens":0,"reasoningOutputTokens":0,"totalTokens":1200},"modelContextWindow":200000}}}""");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed"}}}""");
        await _CollectUntilTurnCompletedAsync(driver);

        // Turn 2 reports NO tokenUsage (e.g. an interrupted turn). Its usage must be null — not turn 1's total
        // leaking in, which the accumulating token meter would then double-count.
        await driver.SendUserMessageAsync("two");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"turn/started","params":{"threadId":"thread-1","turn":{"id":"turn-2"}}}""");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-2","status":"completed"}}}""");

        var events = await _CollectUntilTurnCompletedAsync(driver);
        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.Usage.Should().BeNull();
    }

    // --- helpers -----------------------------------------------------------------------------------------

    private static async Task<PluginSessionStatus> _WaitForStatusAsync(CodexAppServerSessionDriver driver, Func<PluginSessionStatus, bool> predicate)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (driver.Status is { } status && predicate(status))
            {
                return status;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("The driver did not reach the expected status.");
    }

    private static async Task _StartAsync(CodexAppServerSessionDriver driver, FakeCliSubprocess fake, string threadId = "thread-1")
    {
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "thread/start", $$"""{"threadId":"{{threadId}}"}""");
        await startTask;
    }

    private static async Task<JsonElement> _RespondAsync(FakeCliSubprocess fake, string method, string resultJson)
    {
        var request = await _WaitForRequestAsync(fake, method);
        var id = request.GetProperty("id").GetInt64();
        await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"result":{{{resultJson}}}}""");
        return request;
    }

    private static async Task<JsonElement> _WaitForRequestAsync(FakeCliSubprocess fake, string method)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var line = fake.WrittenLines.LastOrDefault(written => written.Contains($"\"method\":\"{method}\""));
            if (line is not null)
            {
                return JsonDocument.Parse(line).RootElement;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"No request for method '{method}' was written.");
    }

    private static Task _WaitForRequestIdAsync(FakeCliSubprocess fake, string method) => _WaitForRequestAsync(fake, method);

    private static async Task<string> _WaitForWrittenLineAsync(FakeCliSubprocess fake, string contains)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var line = fake.WrittenLines.LastOrDefault(written => written.Contains(contains));
            if (line is not null)
            {
                return line;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"No written line containing '{contains}'.");
    }

    private static async Task<PluginSessionEvent> _NextEventAsync(CodexAppServerSessionDriver driver)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in driver.Events.WithCancellation(timeout.Token))
        {
            return evt;
        }

        throw new InvalidOperationException("No event was produced.");
    }

    private static async Task<T> _NextEventOfTypeAsync<T>(CodexAppServerSessionDriver driver) where T : PluginSessionEvent
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in driver.Events.WithCancellation(timeout.Token))
        {
            if (evt is T typed)
            {
                return typed;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} event was produced.");
    }

    private static async Task<List<PluginSessionEvent>> _CollectUntilTurnCompletedAsync(CodexAppServerSessionDriver driver)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<PluginSessionEvent>();
        await foreach (var evt in driver.Events.WithCancellation(timeout.Token))
        {
            events.Add(evt);
            if (evt is PluginTurnCompleted)
            {
                break;
            }
        }

        return events;
    }
}
