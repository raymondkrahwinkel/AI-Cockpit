using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.TestSupport;

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

    // The profile's environment variables (AC-22) ride the environment-carrying StartAsync overload into the
    // spawn, under everything the driver sets itself — the config's CODEX_HOME/auth keep the last word.
    [Fact]
    public async Task Start_LaysTheProfilesEnvironmentUnderTheConfigsOwnVariables()
    {
        var fake = new FakeCliSubprocess();
        var config = new CliAgentConfig(WorkingDirectory: Path.GetTempPath(), ConfigDir: "/home/raymond/.codex-profile");
        await using var driver = new CodexAppServerSessionDriver(() => fake, config, "codex");

        var startTask = driver.StartAsync(
            null, "/work", resumeSessionId: null, options: null, mcpServers: null,
            environment: new Dictionary<string, string>
            {
                ["AI_OS_ROOT"] = "/home/raymond/AI-OS",
                ["CODEX_HOME"] = "/somebody/elses/home",
            },
            CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        Assert.Contains(new KeyValuePair<string, string?>("AI_OS_ROOT", "/home/raymond/AI-OS"), fake.EnvironmentVariables!);
        Assert.Contains(new KeyValuePair<string, string?>("CODEX_HOME", "/home/raymond/.codex-profile"), fake.EnvironmentVariables!);
    }

    [Fact]
    public async Task Start_DoesHandshake_PassesTheCockpitCwd_AndEmitsSessionInitialized()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var startTask = driver.StartAsync("gpt-5-codex", "/work/here", resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
        var threadStart = await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        Assert.Equal("/work/here", threadStart.GetProperty("params").GetProperty("cwd").GetString());
        Assert.Equal("gpt-5-codex", threadStart.GetProperty("params").GetProperty("model").GetString());
        Assert.Contains(fake.WrittenLines, line => line.Contains("\"method\":\"initialized\""));

        var initialized = await _NextEventAsync(driver);
        Assert.IsType<PluginSessionInitialized>(initialized);
        Assert.Equal("thread-1", driver.SessionId);
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
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
        var threadStart = await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        Assert.Equal("workspace-write", threadStart.GetProperty("params").GetProperty("sandbox").GetString());
        Assert.Equal("o3", threadStart.GetProperty("params").GetProperty("model").GetString());
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
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        // The MCP servers ride -c overrides placed before the subcommand, which stays last.
        Assert.True(SequenceAssert.ContainsInOrder(fake.Arguments!, "-c", """mcp_servers.cockpit-orchestrator={ url = "http://127.0.0.1:8765/mcp" }"""));
        Assert.Equal("app-server", fake.Arguments!.Last());

        // The bearer token is never on the command line (that would leak it in /proc/<pid>/cmdline) — only its
        // env-var name is, and the token itself reaches the child through the process environment.
        Assert.DoesNotContain(fake.Arguments!, argument => argument.Contains(token));
        Assert.Contains(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_1", token), fake.EnvironmentVariables!);
    }

    [Fact]
    public async Task Start_WithResume_SendsThreadResume_ForThatThreadId()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var startTask = driver.StartAsync(null, "/work", resumeSessionId: "thread-99", options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
        var resume = await _RespondAsync(fake, "thread/resume", """{"threadId":"thread-99"}""");
        await startTask;

        Assert.Equal("thread-99", resume.GetProperty("params").GetProperty("threadId").GetString());
        Assert.Equal("thread-99", driver.SessionId);
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

        Assert.Equal("Hello, world!", string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text)));
        var completed = Assert.Single(events.OfType<PluginTurnCompleted>());
        Assert.False(completed.IsError);
        // AC-126: turn/completed carries no "final text" of its own on the wire — Result must be the deltas this
        // driver folded, not null, or get_task_result/LastAssistantText see a "finished" turn with no answer.
        Assert.Equal("Hello, world!", completed.Result);
    }

    // AC-126: a turn that never streamed a message (pure tool-use, or a failed turn before any text) must report
    // Result:null rather than an empty string — an empty StringBuilder is not an answer to fold in as one.
    [Fact]
    public async Task SendUserMessage_WithNoAgentMessage_CompletesWithANullResult()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("run the tests");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"turn/started","params":{"threadId":"thread-1","turn":{"id":"turn-1"}}}""");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed"}}}""");

        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Null(Assert.Single(events.OfType<PluginTurnCompleted>()).Result);
    }

    // AC-126: the accumulator is per-turn, not per-session — a second turn's Result must be only its own text, not
    // the first turn's answer prepended to it (which turn/started's reset guards against).
    [Fact]
    public async Task SendUserMessage_OnASecondTurn_DoesNotCarryOverTheFirstTurnsText()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("one");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"turn/started","params":{"threadId":"thread-1","turn":{"id":"turn-1"}}}""");
        await fake.PushStdoutAsync("""{"method":"item/agentMessage/delta","params":{"delta":"first answer","itemId":"i1","threadId":"thread-1","turnId":"turn-1"}}""");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed"}}}""");
        await _CollectUntilTurnCompletedAsync(driver);

        await driver.SendUserMessageAsync("two");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"turn/started","params":{"threadId":"thread-1","turn":{"id":"turn-2"}}}""");
        await fake.PushStdoutAsync("""{"method":"item/agentMessage/delta","params":{"delta":"second answer","itemId":"i2","threadId":"thread-1","turnId":"turn-2"}}""");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-2","status":"completed"}}}""");

        var events = await _CollectUntilTurnCompletedAsync(driver);
        Assert.Equal("second answer", Assert.Single(events.OfType<PluginTurnCompleted>()).Result);
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
        Assert.Equal("cmd-1", permission.ToolUseId);
        Assert.Equal("shell", permission.ToolName);

        await driver.RespondToPermissionAsync("cmd-1", allow: true);

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":55");
        using var document = JsonDocument.Parse(answer);
        Assert.Equal("accept", document.RootElement.GetProperty("result").GetProperty("decision").GetString());
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
        Assert.Equal("decline", document.RootElement.GetProperty("result").GetProperty("decision").GetString());
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
        Assert.Equal("acceptForSession", document.RootElement.GetProperty("result").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ProcessId_ReflectsTheSpawnedAppServerProcess()
    {
        var fake = new FakeCliSubprocess { ProcessId = 9999 };
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // D10: the resource meter measures the codex app-server process this session runs in.
        Assert.Equal(9999, driver.ProcessId);
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
        var completed = Assert.Single(events.OfType<PluginTurnCompleted>());
        Assert.False(completed.IsError);
        Assert.Equal("interrupt", completed.StopReason);
    }

    // AC-126: the operator stopping a turn mid-answer must not throw away the partial answer already streamed —
    // it is what the caller has to show for the interruption, the same as an interrupted OpenAiCompat turn.
    [Fact]
    public async Task TurnCompleted_WithInterruptedStatus_StillCarriesTheTextStreamedSoFar()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"turn/started","params":{"threadId":"thread-1","turn":{"id":"turn-1"}}}""");
        await fake.PushStdoutAsync("""{"method":"item/agentMessage/delta","params":{"delta":"partial an","itemId":"i1","threadId":"thread-1","turnId":"turn-1"}}""");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"interrupted"}}}""");

        var events = await _CollectUntilTurnCompletedAsync(driver);
        Assert.Equal("partial an", Assert.Single(events.OfType<PluginTurnCompleted>()).Result);
    }

    // AC-126: the reasoning trace is a separate wire notification from the visible answer (item/reasoning/*) and
    // must never fold into Result — a caller polling get_task_result would otherwise see Codex's internal
    // deliberation mixed into (or standing in for) its actual answer.
    [Fact]
    public async Task ReasoningDeltas_DoNotLeakIntoTheTurnsResult()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _WaitForRequestIdAsync(fake, "turn/start");
        await fake.PushStdoutAsync("""{"method":"turn/started","params":{"threadId":"thread-1","turn":{"id":"turn-1"}}}""");
        await fake.PushStdoutAsync("""{"method":"item/reasoning/textDelta","params":{"delta":"let me think about this...","itemId":"r1","threadId":"thread-1","turnId":"turn-1"}}""");
        await fake.PushStdoutAsync("""{"method":"item/reasoning/summaryTextDelta","params":{"delta":"analysing the request","itemId":"r1","threadId":"thread-1","turnId":"turn-1"}}""");
        await fake.PushStdoutAsync("""{"method":"item/agentMessage/delta","params":{"delta":"the answer","itemId":"i1","threadId":"thread-1","turnId":"turn-1"}}""");
        await fake.PushStdoutAsync("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed"}}}""");

        var events = await _CollectUntilTurnCompletedAsync(driver);
        Assert.Equal("the answer", Assert.Single(events.OfType<PluginTurnCompleted>()).Result);
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
        Assert.True(document.RootElement.TryGetProperty("error", out _));
        Assert.False(document.RootElement.TryGetProperty("result", out _));
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
        Assert.Equal(25, status.ContextUsedPercent);
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
        Assert.Equal(25, status.ContextUsedPercent);
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
        Assert.Equal(new[]
        {
            new PluginRateLimitWindow("5h", 60, DateTimeOffset.FromUnixTimeSeconds(1800000000), 300),
            new PluginRateLimitWindow("7d", 80, DateTimeOffset.FromUnixTimeSeconds(1800600000), 10080),
        }, status.RateLimits);
    }

    [Fact]
    public async Task SessionInitialized_CarriesTheWorkingDirectory()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var startTask = driver.StartAsync(null, "/work/here", resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        // D3: the session reports its cwd so the host's git-status header and active-cwd observer follow it.
        var initialized = await _NextEventOfTypeAsync<PluginSessionInitialized>(driver);
        Assert.Equal("/work/here", initialized.Cwd);
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
        Assert.Equal("Let me consider", thinking.Thinking);
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
        Assert.Equal(new PluginTokenUsage(1000, 230, 50, 0), Assert.Single(events.OfType<PluginTurnCompleted>()).Usage);
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
        Assert.Null(Assert.Single(events.OfType<PluginTurnCompleted>()).Usage);
    }

    [Fact]
    public async Task LiveOptions_DeclareTheModelsFromTheListing_AndTheEffortLevels()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var startTask = driver.StartAsync("gpt-5-codex", "/work", resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[{"id":"gpt-5-codex","isDefault":true},{"id":"gpt-5"}]}""");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        // D4: the live controls the header renders — the model list read on this connection, opened on the model the
        // session started with, plus the fixed effort levels which open unset (Codex runs its own default).
        var model = Assert.Single(driver.LiveOptions, option => option.Key == "model");
        Assert.Equal(new[] { "gpt-5-codex", "gpt-5" }, model.Choices);
        Assert.Equal("gpt-5-codex", model.DefaultValue);

        var effort = Assert.Single(driver.LiveOptions, option => option.Key == "effort");
        Assert.Equal(new[] { "low", "medium", "high" }, effort.Choices);
        Assert.Null(effort.DefaultValue);
    }

    [Fact]
    public async Task LiveOptions_KeepTheCurrentModelSelectable_WhenTheListingOmitsIt()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        // The session runs a pinned model the public listing does not carry; it must still be among the choices so
        // the panel opens on it rather than blank.
        var startTask = driver.StartAsync("my-pinned-model", "/work", resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[{"id":"gpt-5"}]}""");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        var model = Assert.Single(driver.LiveOptions, option => option.Key == "model");
        Assert.Contains("my-pinned-model", model.Choices);
        Assert.Equal("my-pinned-model", model.DefaultValue);
    }

    [Fact]
    public async Task TurnStart_CarriesTheLiveModelAndEffort_AfterASwitch()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // D4: the operator switches model and effort mid-session; both ride the next turn/start as per-turn overrides.
        await driver.SetLiveOptionAsync("model", "gpt-5");
        await driver.SetLiveOptionAsync("effort", "high");
        await driver.SendUserMessageAsync("go");

        var turn = await _WaitForRequestAsync(fake, "turn/start");
        Assert.Equal("gpt-5", turn.GetProperty("params").GetProperty("model").GetString());
        Assert.Equal("high", turn.GetProperty("params").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task LiveOptions_IncludeTheApprovalPolicyControl()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // D4 inc2: Codex's approval policy is a live control — the simple AskForApproval enum — opening unset so
        // Codex keeps its own default until the operator picks one.
        var approval = Assert.Single(driver.LiveOptions, option => option.Key == "approvalPolicy");
        Assert.Equal(new[] { "untrusted", "on-request", "never" }, approval.Choices);
        Assert.Null(approval.DefaultValue);
    }

    [Fact]
    public async Task TurnStart_CarriesTheApprovalPolicy_AfterASwitch()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // D4 inc2: switching the approval policy rides the next turn/start as a per-turn override, like model/effort.
        await driver.SetLiveOptionAsync("approvalPolicy", "never");
        await driver.SendUserMessageAsync("go");

        var turn = await _WaitForRequestAsync(fake, "turn/start");
        Assert.Equal("never", turn.GetProperty("params").GetProperty("approvalPolicy").GetString());
    }

    [Fact]
    public async Task LiveOptions_IncludeTheSandboxControl_OpenedOnTheLaunchSandbox()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var options = new Dictionary<string, string> { ["sandbox"] = "workspace-write" };
        var startTask = driver.StartAsync(null, "/work", resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        // D4 inc2b: sandbox is a live control offering the same kebab choices as the dialog, opened on the sandbox the
        // session actually launched with (there is always one), unlike effort/approval which open unset.
        var sandbox = Assert.Single(driver.LiveOptions, option => option.Key == "sandbox");
        Assert.Equal(new[] { "read-only", "workspace-write", "danger-full-access" }, sandbox.Choices);
        Assert.Equal("workspace-write", sandbox.DefaultValue);
    }

    [Fact]
    public async Task TurnStart_CarriesTheSandboxPolicyObject_WithItsCamelCaseType_AfterASwitch()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // D4 inc2b: the sandbox override rides turn/start as the tagged-union SandboxPolicy object, keyed by its
        // camelCase type — the kebab choice "danger-full-access" becomes { "type": "dangerFullAccess" }.
        await driver.SetLiveOptionAsync("sandbox", "danger-full-access");
        await driver.SendUserMessageAsync("go");

        var turn = await _WaitForRequestAsync(fake, "turn/start");
        Assert.Equal("dangerFullAccess", turn.GetProperty("params").GetProperty("sandboxPolicy").GetProperty("type").GetString());
    }

    [Fact]
    public async Task TurnStart_CarriesTheLaunchSandbox_AsAPolicyObject_EvenWithoutASwitch()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var options = new Dictionary<string, string> { ["sandbox"] = "workspace-write" };
        var startTask = driver.StartAsync(null, "/work", resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        await driver.SendUserMessageAsync("go");

        // The launch sandbox is re-asserted on every turn as its policy object (like the model), so a turn the
        // operator never touched still runs under the sandbox the session launched with.
        var turn = await _WaitForRequestAsync(fake, "turn/start");
        Assert.Equal("workspaceWrite", turn.GetProperty("params").GetProperty("sandboxPolicy").GetProperty("type").GetString());
    }

    [Fact]
    public async Task TurnStart_OmitsTheSandboxPolicy_WhenTheLiveSandboxIsAnUnknownValue()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");
        await _StartAsync(driver, fake);

        // An unknown sandbox value maps to no policy type, so the override is dropped from the wire rather than
        // sending a bogus type Codex would reject — the session keeps the sandbox it launched with.
        await driver.SetLiveOptionAsync("sandbox", "not-a-real-mode");
        await driver.SendUserMessageAsync("go");

        var turn = await _WaitForRequestAsync(fake, "turn/start");
        Assert.False(turn.GetProperty("params").TryGetProperty("sandboxPolicy", out _));
    }

    [Fact]
    public async Task TurnStart_WithoutASwitch_CarriesTheStartModel_AndNoEffort()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new CodexAppServerSessionDriver(() => fake, _DefaultConfig(), "codex");

        var startTask = driver.StartAsync("gpt-5-codex", "/work", resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
        await _RespondAsync(fake, "thread/start", """{"threadId":"thread-1"}""");
        await startTask;

        await driver.SendUserMessageAsync("go");

        // A turn the operator never touched carries the model the session started on and no effort or approval at all
        // (a null override is dropped from the wire), so Codex keeps its own defaults rather than ones this driver invented.
        var turn = await _WaitForRequestAsync(fake, "turn/start");
        Assert.Equal("gpt-5-codex", turn.GetProperty("params").GetProperty("model").GetString());
        Assert.False(turn.GetProperty("params").TryGetProperty("effort", out _));
        Assert.False(turn.GetProperty("params").TryGetProperty("approvalPolicy", out _));
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
        // The driver lists the live-control models on the same connection right after the handshake (#45 D4); an
        // empty listing keeps the start moving without asserting on the models here.
        await _RespondAsync(fake, "model/list", """{"data":[]}""");
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
