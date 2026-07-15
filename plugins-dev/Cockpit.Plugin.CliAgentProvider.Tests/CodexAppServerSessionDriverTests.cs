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

    // --- helpers -----------------------------------------------------------------------------------------

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
