using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// AC-783: `OpencodeAcpSessionDriver` against a `FakeCliSubprocess` — session lifecycle, the forced
// OPENCODE_CONFIG_CONTENT permission policy, MCP shapes, and stop-reason mapping. Mirrors
// KimiAcpSessionDriverTests' harness pattern, scoped to what differs plus the shared core lifecycle.
public class OpencodeAcpSessionDriverTests
{
    private static OpencodeConfig _DefaultConfig() => new(WorkingDirectory: Path.GetTempPath());

    // --- session lifecycle & permission policy (criterion 3) ------------------------------------------------

    [Fact]
    public async Task Start_SendsSessionNew_WithAbsoluteCwd_AndEmitsSessionInitialized()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var sessionNew = await _RespondAsync(fake, "session/new", """{"sessionId":"ses_1","configOptions":[]}""");
        await startTask;

        Assert.True(Path.IsPathRooted(sessionNew.GetProperty("params").GetProperty("cwd").GetString()));
        Assert.Equal("ses_1", driver.SessionId);

        var initialized = await _NextEventOfTypeAsync<PluginSessionInitialized>(driver);
        Assert.Equal("ses_1", initialized.SessionId);
    }

    // The mechanism criterion 3 depends on: opencode does not ask permission by default (measured live), and
    // an inline session/new "permission" param is silently ignored (measured live) — OPENCODE_CONFIG_CONTENT
    // is the one env var that live-verified reaches opencode's permission engine.
    [Fact]
    public async Task Start_WithNoPermissionModeOption_ForcesAskEverythingViaOpencodeConfigContent()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");

        await _StartAsync(driver, fake);

        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Equal("""{"permission":{"*":"ask"}}""", fake.EnvironmentVariables!["OPENCODE_CONFIG_CONTENT"]);
    }

    // The one host-side mode that means "run fully autonomously" must actually stop asking, not silently keep
    // the forced-ask policy the default path uses — that would defeat the operator's own explicit choice.
    [Fact]
    public async Task Start_WithBypassPermissionsMode_ForcesAllowEverythingInstead()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        var options = new Dictionary<string, string> { [WellKnownPluginSessionOptions.PermissionMode] = "bypassPermissions" };

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"ses_1","configOptions":[{"id":"mode","name":"Session Mode","category":"mode","type":"select","currentValue":"build","options":[{"value":"build","name":"build"},{"value":"plan","name":"plan"}]}]}""");
        var setMode = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("""{"permission":{"*":"allow"}}""", fake.EnvironmentVariables!["OPENCODE_CONFIG_CONTENT"]);
        Assert.Equal("mode", setMode.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("build", setMode.GetProperty("params").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Start_WithPlanPermissionMode_TranslatesToOpencodesOwnPlanMode()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        var options = new Dictionary<string, string> { [WellKnownPluginSessionOptions.PermissionMode] = "plan" };

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"ses_1","configOptions":[{"id":"mode","name":"Session Mode","category":"mode","type":"select","currentValue":"build","options":[{"value":"build","name":"build"},{"value":"plan","name":"plan"}]}]}""");
        var setMode = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("plan", setMode.GetProperty("params").GetProperty("value").GetString());
        // Still asks — plan mode disallows edits at the model level, but whatever it does allow (e.g. reads) is
        // still gated through Cockpit's own consent card.
        Assert.Equal("""{"permission":{"*":"ask"}}""", fake.EnvironmentVariables!["OPENCODE_CONFIG_CONTENT"]);
    }

    [Fact]
    public async Task Start_WithResumeSessionId_SendsSessionResume_NeverSessionLoad()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: "ses_99", options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var resume = await _RespondAsync(fake, "session/resume", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("ses_99", resume.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal("ses_99", driver.SessionId);
        Assert.DoesNotContain(fake.WrittenLines, line => line.Contains("\"method\":\"session/load\""));
    }

    [Fact]
    public async Task Start_SendsMcpServers_StdioWithoutTypeField_HttpWithTypeHttp()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");

        PluginMcpServer[] mcpServers =
        [
            new() { Name = "fs", Command = "npx", Args = ["-y", "@mcp/fs"] },
            new() { Name = "api", Url = "http://x/mcp" },
        ];

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var sessionNew = await _RespondAsync(fake, "session/new", """{"sessionId":"ses_1","configOptions":[]}""");
        await startTask;

        var wireServers = sessionNew.GetProperty("params").GetProperty("mcpServers");
        Assert.False(wireServers[0].TryGetProperty("type", out _));
        Assert.Equal("http", wireServers[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Start_UsesThePerSessionModelOption_OverConfig()
    {
        var fake = new FakeCliSubprocess();
        var config = new OpencodeConfig(WorkingDirectory: Path.GetTempPath(), DefaultModel: "opencode/big-pickle");
        await using var driver = new OpencodeAcpSessionDriver(() => fake, config, "opencode");

        var options = new Dictionary<string, string> { [WellKnownPluginSessionOptions.Model] = "anthropic/claude-sonnet-4-5" };
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"ses_1","configOptions":[]}""");
        var setModel = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("model", setModel.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("anthropic/claude-sonnet-4-5", setModel.GetProperty("params").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Start_WithADefaultModelNotAmongTheOfferedChoices_SkipsTheSetConfigOptionCall()
    {
        var fake = new FakeCliSubprocess();
        var config = new OpencodeConfig(WorkingDirectory: Path.GetTempPath(), DefaultModel: "opencode/retired-model");
        await using var driver = new OpencodeAcpSessionDriver(() => fake, config, "opencode");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, timeout.Token);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"ses_1","configOptions":[{"id":"model","name":"Model","category":"model","type":"select","currentValue":"opencode/big-pickle","options":[{"value":"opencode/big-pickle","name":"Big Pickle"}]}]}""");

        await startTask;

        Assert.DoesNotContain(fake.WrittenLines, line => line.Contains("\"method\":\"session/set_config_option\""));
    }

    [Fact]
    public async Task Start_WithAnAppendSystemPromptOption_ReportsThatItIsNotApplied_WithoutEchoingIt()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        var options = new Dictionary<string, string> { [WellKnownPluginSessionOptions.AppendSystemPrompt] = "You are a secret CEO persona." };

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"ses_1","configOptions":[]}""");
        await startTask;

        var error = await _NextEventOfTypeAsync<PluginSessionError>(driver);
        Assert.Contains("has no way to receive one over ACP", error.Message);
        Assert.DoesNotContain("secret CEO persona", error.Message);
    }

    // --- SetLiveOptionAsync validates against the live snapshot, not a hardcoded id list --------------------

    [Fact]
    public async Task SetLiveOptionAsync_WithAKeyNotInTheLiveSnapshot_SendsNoRequest()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        await _StartAsync(driver, fake, configOptionsJson: """[{"id":"model","name":"Model","category":"model","type":"select","currentValue":"opencode/big-pickle","options":[]}]""");

        await driver.SetLiveOptionAsync("thinking", "high", CancellationToken.None);

        Assert.DoesNotContain(fake.WrittenLines, line => line.Contains("\"configId\":\"thinking\""));
    }

    [Fact]
    public async Task SetLiveOptionAsync_WithAKeyThatIsInTheLiveSnapshot_SendsTheRequest()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        await _StartAsync(driver, fake, configOptionsJson: """[{"id":"mode","name":"Session Mode","category":"mode","type":"select","currentValue":"build","options":[]}]""");

        var setTask = driver.SetLiveOptionAsync("mode", "plan", CancellationToken.None);
        var request = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[]}""");
        await setTask;

        Assert.Equal("plan", request.GetProperty("params").GetProperty("value").GetString());
    }

    // --- usage_update -> Status (criterion 2: opencode DOES report usage, unlike Kimi) -----------------------

    [Fact]
    public async Task UsageUpdate_SetsStatus_FromUsedAndSize()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"ses_1","update":{"sessionUpdate":"usage_update","used":8507,"size":200000,"cost":{"amount":0,"currency":"USD"}}}}""");

        await _WaitUntilAsync(() => driver.Status is not null);
        Assert.NotNull(driver.Status);
        Assert.Equal(8507.0 / 200000 * 100, driver.Status!.ContextUsedPercent);
        Assert.Empty(driver.Status.RateLimits);
    }

    [Fact]
    public async Task UsageUpdate_IsNotForwardedToTheTranscript()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"ses_1","update":{"sessionUpdate":"usage_update","used":100,"size":1000,"cost":{"amount":0,"currency":"USD"}}}}""");
        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"ses_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hi"}}}}""");

        var delta = await _NextEventOfTypeAsync<PluginAssistantTextDelta>(driver);
        Assert.Equal("hi", delta.Text);
    }

    // --- stop reason mapping (criterion 2: full ACP spec enum, not Kimi's own 3-value fold) -----------------

    [Theory]
    [InlineData("end_turn", false)]
    [InlineData("cancelled", false)]
    [InlineData("max_tokens", false)]
    [InlineData("max_turn_requests", false)]
    [InlineData("refusal", true)]
    public async Task SendUserMessage_MapsEveryAcpStopReason(string wireStopReason, bool expectIsError)
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", $$"""{"stopReason":"{{wireStopReason}}"}""");

        var completed = await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);
        Assert.Equal(wireStopReason, completed.StopReason);
        Assert.Equal(expectIsError, completed.IsError);
    }

    // --- permission flow (criterion 3: end to end through Cockpit's own consent card) ------------------------

    [Fact]
    public async Task ToolCall_ThenPermissionRequest_RoutesThroughPluginPermissionRequested_AndRespondsWithTheChosenOption()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("write a file");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"end_turn"}""");

        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"ses_1","update":{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"write","kind":"edit","status":"pending","rawInput":{"filepath":"hello.txt"}}}}""");
        var toolUse = await _NextEventOfTypeAsync<PluginToolUseRequested>(driver);
        Assert.Equal("call_1", toolUse.ToolUseId);

        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"ses_1","toolCall":{"toolCallId":"call_1","title":"hello.txt"},"options":[{"optionId":"once","kind":"allow_once","name":"Allow once"},{"optionId":"always","kind":"allow_always","name":"Always allow"},{"optionId":"reject","kind":"reject_once","name":"Reject"}]}}""");
        var permission = await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);
        Assert.Equal("call_1", permission.ToolUseId);

        await driver.RespondToPermissionAsync("call_1", allow: true);
        var response = await _WaitForRequestAsync(fake, method: null, jsonContains: "\"id\":0");
        Assert.Equal("once", response.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task Interrupt_AnswersEveryOutstandingPermissionRequest_WithCancelled()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("write a file");
        await _WaitForRequestAsync(fake, "session/prompt");

        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"ses_1","toolCall":{"toolCallId":"call_1","title":"hello.txt"},"options":[{"optionId":"once","kind":"allow_once","name":"Allow once"}]}}""");
        await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);

        await driver.InterruptAsync();

        var cancelNotification = await _WaitForRequestAsync(fake, "session/cancel");
        Assert.Equal("ses_1", cancelNotification.GetProperty("params").GetProperty("sessionId").GetString());

        var permissionResponse = await _WaitForRequestAsync(fake, method: null, jsonContains: "\"id\":0");
        Assert.Equal("cancelled", permissionResponse.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
    }

    // --- disposal ------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var fake = new FakeCliSubprocess();
        var driver = new OpencodeAcpSessionDriver(() => fake, _DefaultConfig(), "opencode");
        await _StartAsync(driver, fake);

        await driver.DisposeAsync();
        var secondDispose = async () => await driver.DisposeAsync();

        await secondDispose();
        Assert.True(fake.Disposed);
    }

    // --- helpers ---------------------------------------------------------------------------------------------

    private static async Task _StartAsync(OpencodeAcpSessionDriver driver, FakeCliSubprocess fake, string sessionId = "ses_1", string configOptionsJson = "[]")
    {
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", $$"""{"sessionId":"{{sessionId}}","configOptions":{{configOptionsJson}}}""");
        await startTask;

        // Drains the buffered PluginSessionInitialized so downstream tests start from an empty event channel.
        await _NextEventOfTypeAsync<PluginSessionInitialized>(driver);
    }

    private static async Task<JsonElement> _RespondAsync(FakeCliSubprocess fake, string method, string resultJson)
    {
        var request = await _WaitForRequestAsync(fake, method);
        var id = request.GetProperty("id").GetInt64();
        await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"result":{{{resultJson}}}}""");
        return request;
    }

    private static async Task<JsonElement> _WaitForRequestAsync(FakeCliSubprocess fake, string? method, string? jsonContains = null)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var line = fake.WrittenLines.LastOrDefault(written =>
                (method is null || written.Contains($"\"method\":\"{method}\""))
                && (jsonContains is null || written.Contains(jsonContains)));
            if (line is not null)
            {
                return JsonDocument.Parse(line).RootElement;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"No matching request was written (method={method}, contains={jsonContains}).");
    }

    private static async Task _WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("Condition was never met.");
    }

    private static async Task<T> _NextEventOfTypeAsync<T>(OpencodeAcpSessionDriver driver) where T : PluginSessionEvent
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
}
