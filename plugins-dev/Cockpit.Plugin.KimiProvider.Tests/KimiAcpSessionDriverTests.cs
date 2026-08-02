using System.Globalization;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider.Tests;

// `KimiAcpSessionDriver` against a `FakeCliSubprocess` (AC-269/270/271/272) — proves the
// full session lifecycle without a live `kimi acp`: session/new with an absolute cwd and the MCP stdio
// no-type trap, session/resume (never session/load), a non-blocking prompt whose content streams through
// session/update, permission prompts (including the tunnelled AskUserQuestion) and cancel, live config options,
// and the D12 crash signal.
public class KimiAcpSessionDriverTests
{
    private static KimiConfig _DefaultConfig() => new(WorkingDirectory: Path.GetTempPath());

    // --- session lifecycle & MCP (AC-269) -----------------------------------------------------------------

    [Fact]
    public async Task Start_SendsSessionNew_WithAbsoluteCwd_AndEmitsSessionInitialized()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var sessionNew = await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        await startTask;

        Assert.True(Path.IsPathRooted(sessionNew.GetProperty("params").GetProperty("cwd").GetString()));
        Assert.Equal("session_1", driver.SessionId);

        var initialized = await _NextEventOfTypeAsync<PluginSessionInitialized>(driver);
        Assert.Equal("session_1", initialized.SessionId);
    }

    // P1-7: an ANTHROPIC_* credential the cockpit process itself inherited (from whatever shell launched it)
    // must never reach the spawned kimi acp — Moonshot's own CLI has no business seeing an Anthropic key/token
    // (ClaudeSdkSessionDriver applies the same rule for its own spawn).
    [Fact]
    public async Task Start_ScrubsAnInheritedAnthropicCredential_FromTheChildEnvironment()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-leaked");
        try
        {
            var fake = new FakeCliSubprocess();
            await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
            await _StartAsync(driver, fake);

            var environmentVariables = fake.EnvironmentVariables;
            Assert.NotNull(environmentVariables);
            Assert.Null(environmentVariables["ANTHROPIC_API_KEY"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    // A Claude Code session exports these to mark itself, and CLAUDE_CODE_OAUTH_TOKEN among them is a live
    // credential — a cockpit started from inside such a session inherits the lot, and Moonshot's CLI is the last
    // process that should receive it.
    [Theory]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("CLAUDECODE")]
    [InlineData("CLAUDE_AGENT_ID")]
    public async Task Start_ScrubsAnInheritedClaudeAgentMarker_FromTheChildEnvironment(string variable)
    {
        Environment.SetEnvironmentVariable(variable, "inherited");
        try
        {
            var fake = new FakeCliSubprocess();
            await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
            await _StartAsync(driver, fake);

            var environmentVariables = fake.EnvironmentVariables;
            Assert.NotNull(environmentVariables);
            Assert.Null(environmentVariables[variable]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // The bearer for the cockpit's own MCP endpoints is the one host-controlled variable this driver passes on:
    // the servers it hands kimi authenticate with exactly that key, so scrubbing it would lock the session out
    // of the tools it was just given.
    [Fact]
    public async Task Start_KeepsTheCockpitMcpKey_ForTheServersItHandsTheChild()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        var environment = new Dictionary<string, string> { ["COCKPIT_MCP_KEY"] = "mcp-key" };

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, environment, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        await startTask;

        var environmentVariables = fake.EnvironmentVariables;
        Assert.NotNull(environmentVariables);
        Assert.Equal("mcp-key", environmentVariables["COCKPIT_MCP_KEY"]);
    }

    [Fact]
    public async Task Start_WithResumeSessionId_SendsSessionResume_NeverSessionLoad()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: "session_99", options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var resume = await _RespondAsync(fake, "session/resume", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("session_99", resume.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal("session_99", driver.SessionId);
        Assert.DoesNotContain(fake.WrittenLines, line => line.Contains("\"method\":\"session/load\""));
    }

    // The regression test the brief singles out (D6): a serialized stdio server must carry no "type" field at
    // all, or kimi's adapter drops it silently — proved here on the real session/new request the driver sends.
    [Fact]
    public async Task Start_SendsMcpServers_StdioWithoutTypeField_HttpWithTypeHttp()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        PluginMcpServer[] mcpServers =
        [
            new() { Name = "fs", Command = "npx", Args = ["-y", "@mcp/fs"] },
            new() { Name = "api", Url = "http://x/mcp" },
        ];

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var sessionNew = await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        await startTask;

        var wireServers = sessionNew.GetProperty("params").GetProperty("mcpServers");
        Assert.False(wireServers[0].TryGetProperty("type", out _));
        Assert.Equal("http", wireServers[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Start_UsesThePerSessionModelOption_OverConfig()
    {
        var fake = new FakeCliSubprocess();
        var config = new KimiConfig(WorkingDirectory: Path.GetTempPath(), DefaultModel: "kimi-k1");
        await using var driver = new KimiAcpSessionDriver(() => fake, config, "kimi");

        var options = new Dictionary<string, string> { ["model"] = "kimi-k2" };
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        var setConfig = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("model", setConfig.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("kimi-k2", setConfig.GetProperty("params").GetProperty("value").GetString());
    }

    // P1-5: a stale KimiConfig.DefaultModel the current configOptions snapshot positively excludes must not even
    // be attempted — kimi would earn it a -32602 otherwise (AC-272 "snapshot is authoritative").
    [Fact]
    public async Task Start_WithADefaultModelNotAmongTheOfferedChoices_SkipsTheSetConfigOptionCall()
    {
        var fake = new FakeCliSubprocess();
        var config = new KimiConfig(WorkingDirectory: Path.GetTempPath(), DefaultModel: "kimi-retired-model");
        await using var driver = new KimiAcpSessionDriver(() => fake, config, "kimi");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, timeout.Token);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[{"type":"select","id":"model","name":"Model","currentValue":"kimi-k2","options":[{"value":"kimi-k2","name":"Kimi K2"}]}]}""");

        try
        {
            await startTask;
        }
        catch (OperationCanceledException)
        {
            // Only reached by the unfixed behaviour: it sends the doomed request anyway and hangs awaiting a
            // reply this test never provides — the timeout above bounds that instead of stalling the test run.
        }

        Assert.DoesNotContain(fake.WrittenLines, line => line.Contains("\"method\":\"session/set_config_option\""));
    }

    // P1-5: kimi rejecting the configured default model (a race, or any other reason beyond the snapshot check
    // above) must not fail the whole session start — best-effort, the session simply starts on whatever model
    // kimi's own snapshot already defaulted to.
    [Fact]
    public async Task Start_WhenKimiRejectsTheConfiguredDefaultModelAnyway_DoesNotFailTheWholeSessionStart()
    {
        var fake = new FakeCliSubprocess();
        var config = new KimiConfig(WorkingDirectory: Path.GetTempPath(), DefaultModel: "kimi-k2");
        await using var driver = new KimiAcpSessionDriver(() => fake, config, "kimi");

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[{"type":"select","id":"model","name":"Model","currentValue":"kimi-k1","options":[{"value":"kimi-k2","name":"Kimi K2"}]}]}""");

        var setConfigRequest = await _WaitForRequestAsync(fake, "session/set_config_option");
        var id = setConfigRequest.GetProperty("id").GetInt64();
        await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"error":{"code":-32602,"message":"model rejected"}}""");

        await startTask;
        Assert.Equal("session_1", driver.SessionId);
    }

    [Fact]
    public async Task Start_WithPlanPermissionMode_SetsKimiModeToPlan()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var options = new Dictionary<string, string> { [WellKnownPluginSessionOptions.PermissionMode] = "plan" };
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        var setConfig = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("mode", setConfig.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("plan", setConfig.GetProperty("params").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Start_WithDefaultPermissionMode_SetsKimiModeToDefault()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var options = new Dictionary<string, string> { [WellKnownPluginSessionOptions.PermissionMode] = "default" };
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        var setConfig = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("mode", setConfig.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("default", setConfig.GetProperty("params").GetProperty("value").GetString());
    }

    // P0-4, security: acceptEdits must fail closed to kimi's manual-approval "default" mode. Mapping it to
    // "yolo" would disable session/request_permission entirely — shell/deletes running with no prompt at all,
    // a silent privilege escalation past what a non-destructive-write ceiling is supposed to grant.
    [Fact]
    public async Task Start_WithAcceptEditsPermissionMode_FailsClosedToKimiDefaultMode_NotYolo()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var options = new Dictionary<string, string> { [WellKnownPluginSessionOptions.PermissionMode] = "acceptEdits" };
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        var setConfig = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("mode", setConfig.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("default", setConfig.GetProperty("params").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Start_WithBypassPermissionsPermissionMode_SetsKimiModeToAuto()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var options = new Dictionary<string, string> { [WellKnownPluginSessionOptions.PermissionMode] = "bypassPermissions" };
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        var setConfig = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[]}""");
        await startTask;

        Assert.Equal("mode", setConfig.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("auto", setConfig.GetProperty("params").GetProperty("value").GetString());
    }

    // AC-273: Kimi has no way to receive the host's hidden briefing over ACP, so the operator is told once,
    // in the transcript. A profile identity that vanishes without a trace is the failure mode this prevents.
    [Fact]
    public async Task Start_WithAnAppendSystemPromptOption_ReportsThatItIsNotApplied_WithoutEchoingIt()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var options = new Dictionary<string, string> { [WellKnownPluginSessionOptions.AppendSystemPrompt] = "You are Olaf. Answer in Dutch." };
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        await startTask;

        var notice = await _NextEventOfTypeAsync<PluginSessionError>(driver);
        Assert.Contains("system prompt", notice.Message);
        Assert.Contains("not applied", notice.Message);
        Assert.DoesNotContain("You are Olaf", notice.Message);
    }

    [Fact]
    public async Task Start_WithoutAnAppendSystemPromptOption_ReportsNothing()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        var events = await _CollectForAsync(driver, TimeSpan.FromMilliseconds(150));
        Assert.DoesNotContain(events, evt => evt is PluginSessionError);
    }

    [Fact]
    public async Task Start_WithNoOptionsAndNoConfiguredModel_SendsNoSetConfigOptionCalls()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        Assert.DoesNotContain(fake.WrittenLines, line => line.Contains("\"method\":\"session/set_config_option\""));
    }

    [Fact]
    public async Task InterruptAsync_SendsSessionCancelNotification_AndAnswersPendingPermissionsAsCancelled()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":30,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"}],"toolCall":{"toolCallId":"turn-1:tool-1","title":"shell","content":[]}}}""");
        await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);

        await driver.InterruptAsync();

        var cancelNotification = await _WaitForWrittenLineAsync(fake, "\"method\":\"session/cancel\"");
        using var cancelDocument = JsonDocument.Parse(cancelNotification);
        Assert.False(cancelDocument.RootElement.TryGetProperty("id", out _));
        Assert.Equal("session_1", cancelDocument.RootElement.GetProperty("params").GetProperty("sessionId").GetString());

        var permissionAnswer = await _WaitForWrittenLineAsync(fake, "\"id\":30");
        using var answerDocument = JsonDocument.Parse(permissionAnswer);
        Assert.Equal("cancelled", answerDocument.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
    }

    // A permission request that lands after the cancel is answered where it arrives and never tracked. Besides
    // being the right answer (the operator stopped this turn), it is what bounds the drain loop in
    // InterruptAsync: if a request could still enter the dictionary, a child that keeps sending them would keep
    // that loop running for as long as it liked.
    [Fact]
    public async Task PermissionRequest_ArrivingAfterACancel_IsAnsweredCancelled_WithoutRaisingACard()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.InterruptAsync();
        await _WaitForWrittenLineAsync(fake, "\"method\":\"session/cancel\"");

        await fake.PushStdoutAsync("""{"id":77,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"}],"toolCall":{"toolCallId":"turn-1:tool-late","title":"shell","content":[]}}}""");

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":77");
        using var document = JsonDocument.Parse(answer);
        Assert.Equal("cancelled", document.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());

        var events = await _CollectForAsync(driver, TimeSpan.FromMilliseconds(150));
        Assert.DoesNotContain(events, evt => evt is PluginPermissionRequested);
    }

    [Fact]
    public async Task PermissionRequest_AfterANewTurnStarts_IsTrackedAgain()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.InterruptAsync();
        await _WaitForWrittenLineAsync(fake, "\"method\":\"session/cancel\"");
        await driver.SendUserMessageAsync("carry on");
        await _WaitForWrittenLineAsync(fake, "\"method\":\"session/prompt\"");

        await fake.PushStdoutAsync("""{"id":78,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"}],"toolCall":{"toolCallId":"turn-2:tool-1","title":"shell","content":[]}}}""");

        var requested = await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);
        Assert.Equal("turn-2:tool-1", requested.ToolUseId);
    }

    // D3, and the reason the emit gate exists: kimi sends a tool_call and the permission request for the same id
    // back to back, they arrive on two different pumps, and claiming the id is a separate step from writing the
    // event it produced. A permission card that reaches the host before its tool card has nothing to hang its
    // buttons on. Many ids in one run because a single pass would only sometimes interleave.
    [Fact]
    public async Task ToolCallAndItsPermissionRequest_ArrivingBackToBack_AlwaysReachTheHostToolCardFirst()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        const string toolCallTemplate = """{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"tool_call","toolCallId":"@id@","title":"shell","rawInput":{"command":"ls"}}}}""";
        const string permissionTemplate = """{"id":@rid@,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"}],"toolCall":{"toolCallId":"@id@","title":"shell","content":[]}}}""";

        const int calls = 200;
        for (var index = 0; index < calls; index++)
        {
            var toolCallId = $"tool-{index}";
            await fake.PushStdoutAsync(toolCallTemplate.Replace("@id@", toolCallId, StringComparison.Ordinal));
            await fake.PushStdoutAsync(permissionTemplate
                .Replace("@rid@", (1000 + index).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@id@", toolCallId, StringComparison.Ordinal));
        }

        var events = await _CollectForAsync(driver, TimeSpan.FromSeconds(2));

        var ordering = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < events.Count; index++)
        {
            switch (events[index])
            {
                case PluginToolUseRequested toolUse:
                    ordering.TryAdd(toolUse.ToolUseId!, index);
                    break;
                case PluginPermissionRequested permission:
                    Assert.True(ordering.ContainsKey(permission.ToolUseId!),
                        $"a permission card for {permission.ToolUseId} reached the host before the tool card it belongs to");
                    break;
            }
        }

        Assert.Equal(calls, ordering.Count);
    }

    // A poll whose reply never parses as usage must not leave the capture armed for the rest of the session: the
    // next genuine assistant message that happens to look like a usage line would be swallowed, and a silently
    // missing message is worse than a missing percentage.
    [Fact]
    public async Task UsageCapture_ThatNeverSawAParsableReply_DisarmsAndStopsSwallowingLaterText()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi") { UsageCaptureWindowMilliseconds = 50 };
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"end_turn"}""");
        await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);
        await _WaitForNthRequestAsync(fake, "session/prompt", occurrence: 2);

        // The poll is armed but its reply never arrives in a shape the parser recognises. Once the window has
        // passed, a genuine assistant message must reach the transcript even when it happens to contain the very
        // line the parser looks for — an agent quoting a usage report is not a usage report.
        await Task.Delay(120);
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"The manual's example reads Context: 45,000 / 200,000 (22.5%) — that is the format."}}}}""");

        var delta = await _NextEventOfTypeAsync<PluginAssistantTextDelta>(driver);
        Assert.Contains("that is the format", delta.Text);
        Assert.Null(driver.Status);
    }

    // D12: a process end that is NOT our own dispose must surface an error and a failed turn before the
    // transcript just goes quiet — order asserted, not just presence.
    [Fact]
    public async Task ProcessCrash_EmitsSessionErrorThenTurnCompletedWithIsError_BeforeTheChannelEnds()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        fake.CompleteStdout(exitCode: 1);

        var events = new List<PluginSessionEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in driver.Events.WithCancellation(timeout.Token))
        {
            events.Add(evt);
        }

        Assert.Equal(2, System.Linq.Enumerable.Count(events));
        Assert.IsType<PluginSessionError>(events[0]);
        Assert.True(Assert.IsType<PluginTurnCompleted>(events[1]).IsError);
    }

    // --- disposal (P1-6) -------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        var fake = new FakeCliSubprocess();
        var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.DisposeAsync();
        var secondDispose = () => driver.DisposeAsync().AsTask();

        await secondDispose();
    }

    // P1-6: DisposeAsync must wait for the fire-and-forget trailing /usage poll to actually finish releasing
    // _promptGate before disposing it — otherwise Release() (in _PollContextUsageAsync's finally, which has no
    // catch of its own) can run against an already-disposed gate on whichever thread-pool thread eventually
    // runs that continuation, an unobserved task exception nobody awaits in production.
    [Fact]
    public async Task DisposeAsync_AwaitsThePendingUsagePollTask_BeforeReturning()
    {
        var fake = new FakeCliSubprocess();
        var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"completed"}""");
        await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);

        // The trailing /usage poll's own session/prompt is now outstanding, holding _promptGate — deliberately
        // never answered here, so its continuation only fires once _connection.DisposeAsync() (inside
        // DisposeAsync itself) faults the outstanding request.
        await _WaitForNthRequestAsync(fake, "session/prompt", occurrence: 2);
        var pollTask = driver.PendingUsagePollTaskForTests;

        await driver.DisposeAsync();

        Assert.NotNull(pollTask);
        Assert.True(pollTask!.IsCompleted, "DisposeAsync must not return before the pending usage-poll task has finished releasing the prompt gate");
        var awaitPollTask = () => pollTask;
        var pollTaskException = await Record.ExceptionAsync(awaitPollTask);
        Assert.False(pollTaskException is ObjectDisposedException, "the gate must not be disposed while the poll task is still trying to release it");
    }

    // --- event translation (AC-270) -----------------------------------------------------------------------

    [Fact]
    public async Task SendUserMessage_StreamsTextDeltas_ThenCompletesTheTurn()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _WaitForRequestAsync(fake, "session/prompt");
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello"}}}}""");

        // Wait for the delta to actually land before triggering the reply: session/prompt's reply and the
        // notification pump are two independently scheduled consumers of the same wire order, so pushing the
        // reply before the delta is observed races the pump's own scheduling rather than proving anything about
        // the driver.
        var delta = await _NextEventOfTypeAsync<PluginAssistantTextDelta>(driver);
        Assert.Equal("Hello", delta.Text);

        await _RespondAsync(fake, "session/prompt", """{"stopReason":"completed"}""");
        var completed = await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);
        Assert.False(completed.IsError);
    }

    [Fact]
    public async Task ReasoningDelta_IsSurfacedAsAThinkingEvent()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("think");
        await _WaitForRequestAsync(fake, "session/prompt");
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Let me consider"}}}}""");

        var thinking = await _NextEventOfTypeAsync<PluginAssistantThinkingDelta>(driver);
        Assert.Equal("Let me consider", thinking.Thinking);
    }

    // D4: the lazy tool_call (status "pending", only the tool name as title) must still yield exactly one
    // PluginToolUseRequested — the tool_call_update that refines it must not produce a second one.
    [Fact]
    public async Task LazyToolCall_ThenToolCallUpdate_ProducesExactlyOnePluginToolUseRequested()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("read file");
        await _WaitForRequestAsync(fake, "session/prompt");
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","status":"pending"}}}""");
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"in_progress","title":"Read file.txt","rawInput":{"path":"file.txt"}}}}""");
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"file contents"}}]}}}""");

        // Wait for the whole notification-driven sequence (drained up to the terminal ToolResult) before
        // triggering the reply — same reasoning as the text-delta test above: the reply and the notification
        // pump are independently scheduled, so this is what actually proves "exactly one card", not a race.
        var events = await _CollectUntilAsync(driver, evt => evt is PluginToolResult);
        Assert.Equal("turn-1:tool-1", Assert.Single(events.OfType<PluginToolUseRequested>()).ToolUseId);
        Assert.Equal("file contents", Assert.Single(events.OfType<PluginToolResult>()).Content);

        await _RespondAsync(fake, "session/prompt", """{"stopReason":"completed"}""");
        await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);
    }

    [Fact]
    public async Task ConfigOptionUpdate_ShrinkingTheSet_RefreshesLiveOptionsWithoutBreaking()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        const string startingConfigOptions = """[{"type":"select","id":"model","name":"Model","currentValue":"kimi-k2","options":[]},{"type":"select","id":"thinking","name":"Thinking","currentValue":"medium","options":[{"value":"off","name":"Off"},{"value":"medium","name":"Medium"}]},{"type":"select","id":"mode","name":"Mode","currentValue":"default","options":[]}]""";
        await _StartAsync(driver, fake, configOptionsJson: startingConfigOptions);

        Assert.Contains(driver.LiveOptions, option => option.Key == "thinking");

        // The model switched to one with no thinking support: the next config_option_update carries only model/mode.
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"config_option_update","configOptions":[{"type":"select","id":"model","name":"Model","currentValue":"kimi-k1","options":[]},{"type":"select","id":"mode","name":"Mode","currentValue":"default","options":[]}]}}}""");

        await _WaitForLiveOptionsAsync(driver, current => !current.Any(option => option.Key == "thinking"));

        Assert.Equal(2, System.Linq.Enumerable.Count(driver.LiveOptions));
    }

    // --- permissions (AC-271) ------------------------------------------------------------------------------

    [Fact]
    public async Task PermissionRequest_CarriesTheToolCallIdVerbatim_EvenWithoutAPriorToolUse()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":10,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"},{"optionId":"approve_always","name":"Approve for this session","kind":"allow_always"},{"optionId":"reject","name":"Reject","kind":"reject_once"}],"toolCall":{"toolCallId":"turn-1:tool-9","title":"shell","content":[]}}}""");

        var permission = await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);
        Assert.Equal("turn-1:tool-9", permission.ToolUseId);
        Assert.Equal("shell", permission.ToolName);
    }

    // P1-3, trigger (c), D3: a permission request for a toolCallId that never had a prior tool_call must still
    // produce a PluginToolUseRequested — before the PluginPermissionRequested — or the host has no matching
    // tool-use card to attach the approval buttons to.
    [Fact]
    public async Task PermissionRequest_WithoutAPriorToolCall_IsPrecededByAPluginToolUseRequested_ForTheSameId()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":21,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"}],"toolCall":{"toolCallId":"turn-1:tool-21","title":"shell","content":[]}}}""");

        var events = await _CollectUntilAsync(driver, evt => evt is PluginPermissionRequested);
        Assert.Equal(2, System.Linq.Enumerable.Count(events));
        Assert.Equal("turn-1:tool-21", Assert.IsType<PluginToolUseRequested>(events[0]).ToolUseId);
        Assert.Equal("turn-1:tool-21", Assert.IsType<PluginPermissionRequested>(events[1]).ToolUseId);
    }

    [Fact]
    public async Task RespondToPermission_Allow_SelectsTheOptionIdMatchingAllowOnceKind()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":11,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"},{"optionId":"approve_always","name":"Approve for this session","kind":"allow_always"},{"optionId":"reject","name":"Reject","kind":"reject_once"}],"toolCall":{"toolCallId":"turn-1:tool-1","title":"shell","content":[]}}}""");
        await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);

        await driver.RespondToPermissionAsync("turn-1:tool-1", allow: true);

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":11");
        using var document = JsonDocument.Parse(answer);
        Assert.Equal("selected", document.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("approve_once", document.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task RespondToPermission_Deny_SelectsTheOptionIdMatchingRejectOnceKind()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":12,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"},{"optionId":"reject","name":"Reject","kind":"reject_once"}],"toolCall":{"toolCallId":"turn-1:tool-2","title":"shell","content":[]}}}""");
        await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);

        await driver.RespondToPermissionAsync("turn-1:tool-2", allow: false);

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":12");
        using var document = JsonDocument.Parse(answer);
        Assert.Equal("reject", document.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task AllowPermissionAlways_SelectsTheOptionIdMatchingAllowAlwaysKind()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":13,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"},{"optionId":"approve_always","name":"Approve for this session","kind":"allow_always"}],"toolCall":{"toolCallId":"turn-1:tool-3","title":"shell","content":[]}}}""");
        await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);

        await driver.AllowPermissionAlwaysAsync("turn-1:tool-3");

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":13");
        using var document = JsonDocument.Parse(answer);
        Assert.Equal("approve_always", document.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    // D-permissions: the plan_review namespace uses plan_approve (not approve_once) for the "allow_once" kind —
    // proves the lookup reads the offered optionId rather than assuming the canonical one always applies.
    [Fact]
    public async Task RespondToPermission_UsesTheOfferedOptionId_NotTheCanonicalOne_WhenTheNamespaceDiffers()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":14,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"plan_approve","name":"Approve plan","kind":"allow_once"}],"toolCall":{"toolCallId":"turn-1:plan-1","title":"ExitPlanMode","content":[]}}}""");
        await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);

        await driver.RespondToPermissionAsync("turn-1:plan-1", allow: true);

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":14");
        using var document = JsonDocument.Parse(answer);
        Assert.Equal("plan_approve", document.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task TunneledAskUserQuestion_IsSurfacedWithItsTitle_NotAsAToolPermission()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":15,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"q0_a","name":"Option A","kind":"allow_once"}],"toolCall":{"toolCallId":"turn-1:q-1","title":"AskUserQuestion","content":[]}}}""");

        var permission = await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);
        Assert.Equal("AskUserQuestion", permission.ToolName);
    }

    // P0-5: a child process reusing a toolCallId that already has an outstanding approval must not overwrite
    // it — that would let the second request answer whichever card the operator is currently looking at for
    // the first (confused deputy). The duplicate is rejected with a JSON-RPC error, and the original entry
    // (and its own answer) stays intact.
    [Fact]
    public async Task PermissionRequest_WithADuplicateToolCallId_IsRejected_AndTheOriginalStaysAnswerable()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":16,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"}],"toolCall":{"toolCallId":"turn-1:tool-dup","title":"shell","content":[]}}}""");
        await _NextEventOfTypeAsync<PluginPermissionRequested>(driver);

        await fake.PushStdoutAsync("""{"id":17,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"reject","name":"Reject","kind":"reject_once"}],"toolCall":{"toolCallId":"turn-1:tool-dup","title":"shell","content":[]}}}""");

        var duplicateAnswer = await _WaitForWrittenLineAsync(fake, "\"id\":17");
        using var duplicateDocument = JsonDocument.Parse(duplicateAnswer);
        Assert.True(duplicateDocument.RootElement.TryGetProperty("error", out var error), "a reused toolCallId must be rejected, not silently overwrite the first request");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());

        await driver.RespondToPermissionAsync("turn-1:tool-dup", allow: true);

        var originalAnswer = await _WaitForWrittenLineAsync(fake, "\"id\":16");
        using var originalDocument = JsonDocument.Parse(originalAnswer);
        Assert.Equal("approve_once", originalDocument.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    // P1-1: a session/request_permission missing toolCall.toolCallId is blocking (protocol §5) — silently
    // dropping it leaves the agent waiting forever with no card and no error. It must be answered with a
    // JSON-RPC error instead, and the pump must keep working afterward.
    [Fact]
    public async Task MalformedPermissionRequest_WithoutToolCallId_IsAnsweredWithAJsonRpcError()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":18,"method":"session/request_permission","params":{"sessionId":"session_1","options":[],"toolCall":{"title":"shell","content":[]}}}""");

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":18");
        using var document = JsonDocument.Parse(answer);
        Assert.True(document.RootElement.TryGetProperty("error", out var error));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
    }

    // P1-9: an unbounded _pendingApprovals dictionary is an OOM vector on untrusted stdout — a runaway/malicious
    // kimi process could flood session/request_permission faster than the operator could ever answer. Past the
    // cap, a new request is rejected outright rather than tracked.
    [Fact]
    public async Task PermissionRequest_PastTheMaxPendingApprovalsCap_IsRejected()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        for (var i = 0; i < KimiAcpSessionDriver.MaxPendingApprovals; i++)
        {
            await fake.PushStdoutAsync($$$$"""{"id":{{{{1000 + i}}}},"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"}],"toolCall":{"toolCallId":"turn-1:tool-{{{{i}}}}","title":"shell","content":[]}}}""");
        }

        // Wait until every one of the cap's worth of requests has actually been tracked (surfaced as a
        // PluginPermissionRequested) before sending the one that should overflow it.
        var seen = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in driver.Events.WithCancellation(timeout.Token))
        {
            if (evt is PluginPermissionRequested)
            {
                seen++;
                if (seen == KimiAcpSessionDriver.MaxPendingApprovals)
                {
                    break;
                }
            }
        }

        await fake.PushStdoutAsync("""{"id":9999,"method":"session/request_permission","params":{"sessionId":"session_1","options":[{"optionId":"approve_once","name":"Approve once","kind":"allow_once"}],"toolCall":{"toolCallId":"turn-1:tool-overflow","title":"shell","content":[]}}}""");

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":9999");
        using var document = JsonDocument.Parse(answer);
        Assert.True(document.RootElement.TryGetProperty("error", out var error), "a request past the cap must be rejected, not tracked");
        Assert.Equal(-32000, error.GetProperty("code").GetInt32());
    }

    // D11 + the regression test the brief singles out: an unmodelled reverse-request gets -32601, and the pump
    // must keep living afterward — proved with a subsequent notification that still arrives.
    [Fact]
    public async Task UnmodeledServerRequest_IsAnsweredWithAJsonRpcError_AndThePumpKeepsLiving()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"id":20,"method":"fs/read_text_file","params":{"sessionId":"session_1","path":"/x"}}""");

        var answer = await _WaitForWrittenLineAsync(fake, "\"id\":20");
        using var document = JsonDocument.Parse(answer);
        Assert.True(document.RootElement.TryGetProperty("error", out var error));
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("result", out _));

        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"still alive"}}}}""");
        var delta = await _NextEventOfTypeAsync<PluginAssistantTextDelta>(driver);
        Assert.Equal("still alive", delta.Text);
    }

    [Fact]
    public async Task NotificationWithoutParams_DoesNotKillThePump()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await fake.PushStdoutAsync("""{"method":"session/update"}""");
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"survived"}}}}""");

        var delta = await _NextEventOfTypeAsync<PluginAssistantTextDelta>(driver);
        Assert.Equal("survived", delta.Text);
    }

    // --- live config options (AC-272) ---------------------------------------------------------------------

    [Fact]
    public async Task SetLiveOptionAsync_WithAnInvalidConfigId_NeverReachesTheAgent()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SetLiveOptionAsync("not-a-real-config-id", "whatever");

        Assert.DoesNotContain(fake.WrittenLines, line => line.Contains("\"method\":\"session/set_config_option\""));
    }

    [Fact]
    public async Task SetLiveOptionAsync_Mode_SendsSetConfigOption_AndRefreshesLiveOptions()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        var setTask = driver.SetLiveOptionAsync("mode", "yolo");
        var request = await _RespondAsync(fake, "session/set_config_option", """{"configOptions":[{"type":"select","id":"mode","name":"Mode","currentValue":"yolo","options":[{"value":"yolo","name":"YOLO"}]}]}""");
        await setTask;

        Assert.Equal("mode", request.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("yolo", request.GetProperty("params").GetProperty("value").GetString());
        Assert.Equal("yolo", Assert.Single(driver.LiveOptions, option => option.Key == "mode").DefaultValue);
    }

    // --- stopReason mapping (D7, protocol §3/§12) -----------------------------------------------------------
    // The wire carries only end_turn | cancelled | refusal — never Kimi's internal SDK TurnEndReason names
    // (completed/blocked/failed). These four tests replace an earlier version that sent those internal names
    // over the fake wire (a wire form Kimi never actually produces) and never exercised "refusal" at all.

    // D7: end_turn also covers a turn that genuinely failed underneath — Kimi maps that case onto the same
    // end_turn value a successful turn gets, so this single mapping stands for both; there is no wire signal
    // that tells them apart. This locks the honest limitation in so nobody "fixes" it later without a real
    // wire signal to base the fix on.
    [Fact]
    public async Task StopReason_EndTurn_IsNotAnError()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"end_turn"}""");

        var completed = await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);
        Assert.False(completed.IsError);
        Assert.Equal("end_turn", completed.StopReason);
    }

    [Fact]
    public async Task StopReason_Cancelled_IsNotAnError()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"cancelled"}""");

        var completed = await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);
        Assert.False(completed.IsError);
        Assert.Equal("cancelled", completed.StopReason);
    }

    // P0-3: refusal is the one wire value that must actually surface as an error — a refused/filtered turn
    // reported as a success is exactly what D7 wants to avoid.
    [Fact]
    public async Task StopReason_Refusal_IsMappedToRefusal_WithIsErrorTrue()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"refusal"}""");

        var completed = await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);
        Assert.True(completed.IsError);
        Assert.Equal("refusal", completed.StopReason);
    }

    [Fact]
    public async Task StopReason_UnrecognisedValue_DefaultsToEndTurn_AndIsNotAnError()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"some_future_value"}""");

        var completed = await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);
        Assert.False(completed.IsError);
        Assert.Equal("end_turn", completed.StopReason);
    }

    // --- usage / context percentage (AC-274) ---------------------------------------------------------------

    // The single most important test of this sub: a /usage poll's reply must never reach the transcript.
    [Fact]
    public async Task UsagePoll_AfterATurnCompletes_NeverEmitsATextDeltaOrAnExtraTurnCompleted()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"completed"}""");
        var turnCompleted = await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);
        Assert.False(turnCompleted.IsError);

        var usageRequest = await _WaitForNthRequestAsync(fake, "session/prompt", occurrence: 2);
        Assert.Equal("/usage", usageRequest.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        var usageRequestId = usageRequest.GetProperty("id").GetInt64();

        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Session usage:\n- Context: 45,000 / 200,000 (22.5%)"}}}}""");
        await fake.PushStdoutAsync($$$"""{"id":{{{usageRequestId}}},"result":{"stopReason":"end_turn"}}""");

        // P1-14: synchronize deterministically on the positive signal that the poll's chunk was actually
        // consumed (Status becomes non-null) before asserting silence — a fixed sleep-and-hope window alone
        // would pass just as well if the poll simply had not finished processing yet, proving nothing. Only
        // once success is confirmed does the short window below meaningfully check for a leak alongside it.
        await _WaitForStatusAsync(driver, status => status is not null);

        var strayEvents = await _CollectForAsync(driver, TimeSpan.FromMilliseconds(100));
        Assert.Empty(strayEvents);
    }

    [Fact]
    public async Task UsagePoll_AfterATurnCompletes_FillsContextUsedPercent_AndLeavesRateLimitsEmpty()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"completed"}""");
        await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);

        var usageRequestId = (await _WaitForNthRequestAsync(fake, "session/prompt", occurrence: 2)).GetProperty("id").GetInt64();
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Session usage:\n- Context: 45,000 / 200,000 (22.5%)"}}}}""");
        await fake.PushStdoutAsync($$$"""{"id":{{{usageRequestId}}},"result":{"stopReason":"end_turn"}}""");

        await _WaitForStatusAsync(driver, status => status is not null);

        Assert.Equivalent(new PluginSessionStatus(22.5, RateLimits: []), driver.Status);
    }

    // P0-2: only a chunk that actually parses as the usage line is swallowed while capturing — a chunk that
    // does not parse (a broken /usage reply, or a real turn's own trailing text racing the poll) must still
    // reach the transcript rather than vanish silently. This replaces the previous version of this test, which
    // asserted total silence on an unparsable reply — that was the wrong wire form: it cemented the swallow-
    // everything-while-capturing bug this fix removes.
    [Fact]
    public async Task UsagePoll_WithAnUnparsableReply_StillReachesTheTranscript_AndSetsNoStatus()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"completed"}""");
        await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);

        var usageRequestId = (await _WaitForNthRequestAsync(fake, "session/prompt", occurrence: 2)).GetProperty("id").GetInt64();
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"this is not a usage report"}}}}""");
        await fake.PushStdoutAsync($$$"""{"id":{{{usageRequestId}}},"result":{"stopReason":"end_turn"}}""");

        var delta = await _NextEventOfTypeAsync<PluginAssistantTextDelta>(driver);
        Assert.Equal("this is not a usage report", delta.Text);
        Assert.Null(driver.Status);
    }

    // P0-2 regression: a real turn's own chunk that happens to arrive while the trailing /usage poll is
    // capturing must not be swallowed just because the flag is set — only a chunk that actually parses as the
    // usage line may be. Without the parse gate, the driver used to consume the first agent_message_chunk it
    // saw while capturing regardless of content, discarding real assistant text.
    [Fact]
    public async Task ChunkArrivingWhileUsageCaptureIsInFlight_ButNotParsingAsUsage_IsNotSwallowed()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("hi");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"completed"}""");
        await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);

        // The trailing /usage poll's own session/prompt is now outstanding — _capturingUsageResponse is set —
        // before it settles, a chunk that is not the usage line arrives.
        await _WaitForNthRequestAsync(fake, "session/prompt", occurrence: 2);
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"actual trailing turn text"}}}}""");

        var delta = await _NextEventOfTypeAsync<PluginAssistantTextDelta>(driver);
        Assert.Equal("actual trailing turn text", delta.Text);
    }

    // P0-1: a failed /usage round must clear the capturing flag itself, or it stays stuck true and silently
    // swallows the next chunk that happens to parse as a Context line, mistaking ordinary transcript content
    // from a later, unrelated turn for a stale usage answer.
    [Fact]
    public async Task UsagePoll_WhenTheRpcFails_ClearsTheCapturingFlag_SoALaterMatchingChunkIsNotSwallowed()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("first");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"completed"}""");
        await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);

        var usageRequestId = (await _WaitForNthRequestAsync(fake, "session/prompt", occurrence: 2)).GetProperty("id").GetInt64();
        await fake.PushStdoutAsync($$$"""{"id":{{{usageRequestId}}},"error":{"code":-32000,"message":"boom"}}""");

        await driver.SendUserMessageAsync("second");
        await _WaitForNthRequestAsync(fake, "session/prompt", occurrence: 3);
        await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"- Context: 1,000 / 2,000 (50.0%)"}}}}""");

        var delta = await _NextEventOfTypeAsync<PluginAssistantTextDelta>(driver);
        Assert.Equal("- Context: 1,000 / 2,000 (50.0%)", delta.Text);
    }

    // Proves the shared prompt gate (AC-274): a second real message fired the instant the first turn's
    // PluginTurnCompleted lands races the driver's own trailing /usage poll for the same session/prompt slot —
    // whichever wins, the other's reply must still land correctly, never swallowed by the usage-capture buffer.
    [Fact]
    public async Task SendUserMessage_RightAfterAPreviousTurnCompletes_IsNotSwallowedByThePendingUsagePoll()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");
        await _StartAsync(driver, fake);

        await driver.SendUserMessageAsync("first");
        await _RespondAsync(fake, "session/prompt", """{"stopReason":"completed"}""");
        await _NextEventOfTypeAsync<PluginTurnCompleted>(driver);

        await driver.SendUserMessageAsync("second");

        for (var occurrence = 2; occurrence <= 3; occurrence++)
        {
            var request = await _WaitForNthRequestAsync(fake, "session/prompt", occurrence);
            var promptText = request.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString();
            var id = request.GetProperty("id").GetInt64();

            if (promptText == "/usage")
            {
                await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"- Context: 10,000 / 200,000 (5.0%)"}}}}""");
                await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"result":{"stopReason":"end_turn"}}""");
            }
            else
            {
                await fake.PushStdoutAsync("""{"method":"session/update","params":{"sessionId":"session_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Second reply"}}}}""");
                await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"result":{"stopReason":"completed"}}""");
            }
        }

        var delta = await _NextEventOfTypeAsync<PluginAssistantTextDelta>(driver);
        Assert.Equal("Second reply", delta.Text);
    }

    // --- auth surface (P1-10) -----------------------------------------------------------------------------

    // P1-10a: authMethods (protocol §1 — kimi advertises exactly one, the type:"terminal" kimi acp --login
    // flow) must be preserved alongside agentCapabilities, not discarded.
    [Fact]
    public async Task Start_PreservesAuthMethods_FromTheInitializeResponse()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", """{"authMethods":[{"id":"login","type":"terminal","name":"Login with Kimi account"}]}""");
        await _RespondAsync(fake, "session/new", """{"sessionId":"session_1","configOptions":[]}""");
        await startTask;

        Assert.NotNull(driver.AuthMethods);
        Assert.Equal(1, driver.AuthMethods!.Value.GetArrayLength());
        Assert.Equal("login", driver.AuthMethods!.Value.EnumerateArray().First().GetProperty("id").GetString());
    }

    // P1-10b: protocol §1 — session/new fails with the JSON-RPC error -32000 (authRequired) when kimi has no
    // usable token on disk. That must surface as an actionable message naming both routes past it (an API key
    // in the provider config, or kimi acp --login), never the raw JSON-RPC error text — both via the thrown
    // exception (what SessionViewModel shows as "Failed to start: …", P1-8's precedent) and as a PluginSessionError.
    [Fact]
    public async Task Start_WhenSessionNewFailsWithAuthRequired_ThrowsAndEmitsAnActionableMessage()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var sessionNewRequest = await _WaitForRequestAsync(fake, "session/new");
        var id = sessionNewRequest.GetProperty("id").GetInt64();
        await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"error":{"code":-32000,"message":"authRequired"}}""");

        var thrown = await Assert.ThrowsAsync<KimiAcpException>(() => startTask);
        Assert.Contains("kimi acp --login", thrown.Message);
        Assert.Contains("API key", thrown.Message);
        Assert.DoesNotContain("{", thrown.Message);

        var error = await _NextEventOfTypeAsync<PluginSessionError>(driver);
        Assert.Contains("kimi acp --login", error.Message);
        Assert.Contains("API key", error.Message);
    }

    // P1-10b regression: an authRequired failure on the JSON-RPC error must not be confused with any other
    // JSON-RPC error code kimi could return on session/new — only -32000 gets the actionable auth message.
    [Fact]
    public async Task Start_WhenSessionNewFailsWithAnUnrelatedError_DoesNotRewriteTheMessage()
    {
        var fake = new FakeCliSubprocess();
        await using var driver = new KimiAcpSessionDriver(() => fake, _DefaultConfig(), "kimi");

        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        var sessionNewRequest = await _WaitForRequestAsync(fake, "session/new");
        var id = sessionNewRequest.GetProperty("id").GetInt64();
        await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"error":{"code":-32602,"message":"invalid params"}}""");

        var thrown = await Assert.ThrowsAsync<KimiAcpException>(() => startTask);
        Assert.Equal("kimi acp error -32602: invalid params", thrown.Message);
        Assert.DoesNotContain("kimi acp --login", thrown.Message);
    }

    // --- helpers -----------------------------------------------------------------------------------------

    private static async Task _StartAsync(KimiAcpSessionDriver driver, FakeCliSubprocess fake, string sessionId = "session_1", string configOptionsJson = "[]")
    {
        var startTask = driver.StartAsync(null, Path.GetTempPath(), resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "session/new", $$"""{"sessionId":"{{sessionId}}","configOptions":{{configOptionsJson}}}""");
        await startTask;

        // Drains the buffered PluginSessionInitialized so downstream tests (and the crash test's order
        // assertion) start from an empty event channel.
        await _NextEventOfTypeAsync<PluginSessionInitialized>(driver);
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

    // Unlike _WaitForRequestAsync (which always returns the latest match), this waits for the Nth occurrence of
    // a method — needed once a test expects two calls to the same method (a real turn's session/prompt followed
    // by the driver's own trailing /usage poll) and cares which is which.
    private static async Task<JsonElement> _WaitForNthRequestAsync(FakeCliSubprocess fake, string method, int occurrence)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var matches = fake.WrittenLines.Where(written => written.Contains($"\"method\":\"{method}\"")).ToList();
            if (matches.Count >= occurrence)
            {
                return JsonDocument.Parse(matches[occurrence - 1]).RootElement;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"Fewer than {occurrence} requests for method '{method}' were written.");
    }

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

    private static async Task _WaitForLiveOptionsAsync(KimiAcpSessionDriver driver, Func<IReadOnlyList<PluginSessionLaunchOption>, bool> predicate)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (predicate(driver.LiveOptions))
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("LiveOptions did not reach the expected shape.");
    }

    private static async Task _WaitForStatusAsync(KimiAcpSessionDriver driver, Func<PluginSessionStatus?, bool> predicate)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (predicate(driver.Status))
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("Status did not reach the expected shape.");
    }

    // Reads whatever the event channel produces during a fixed window and returns it — used to prove a
    // negative ("nothing further arrived") rather than to wait for a specific event.
    private static async Task<List<PluginSessionEvent>> _CollectForAsync(KimiAcpSessionDriver driver, TimeSpan window)
    {
        var events = new List<PluginSessionEvent>();
        using var timeout = new CancellationTokenSource(window);
        try
        {
            await foreach (var evt in driver.Events.WithCancellation(timeout.Token))
            {
                events.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the window elapsed with nothing further to read.
        }

        return events;
    }

    private static async Task<T> _NextEventOfTypeAsync<T>(KimiAcpSessionDriver driver) where T : PluginSessionEvent
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

    private static async Task<List<PluginSessionEvent>> _CollectUntilAsync(KimiAcpSessionDriver driver, Func<PluginSessionEvent, bool> stopPredicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<PluginSessionEvent>();
        await foreach (var evt in driver.Events.WithCancellation(timeout.Token))
        {
            events.Add(evt);
            if (stopPredicate(evt))
            {
                break;
            }
        }

        return events;
    }
}
