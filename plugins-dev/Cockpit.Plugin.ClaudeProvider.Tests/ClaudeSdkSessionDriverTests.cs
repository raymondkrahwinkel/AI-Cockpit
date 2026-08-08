using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeSdkSessionDriver` (Fase 4, SDK route) driven against a `FakeClaudeSdkSubprocess` —
// the turn-taking and, above all, the in-band permission round-trip that replaces the host's HTTP MCP permission
// server: a `can_use_tool` control_request surfaces as `PluginPermissionRequested`, and the
// operator's answer is written back as a `control_response` echoing the request's own `request_id`.
// The live CLI end (that it emits `can_use_tool` for this spawn) needs a manual eyeball check; everything the
// cockpit does with the line is proven here.
public class ClaudeSdkSessionDriverTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("claude-sdk-driver-tests").FullName;

    [Fact]
    public async Task CanUseTool_SurfacesPermissionRequested_ThenRespondEchoesRequestId()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        // StartAsync puts an SDK client on the control channel first (so the CLI routes approvals here), then applies
        // the launch effort as the session's initial thinking-token budget (default medium).
        Assert.Equal(2, System.Linq.Enumerable.Count(fake.WrittenLines));
        Assert.Equal("initialize", JsonDocument.Parse(fake.WrittenLines[0]).RootElement.GetProperty("request").GetProperty("subtype").GetString());
        Assert.Equal("set_max_thinking_tokens", JsonDocument.Parse(fake.WrittenLines[1]).RootElement.GetProperty("request").GetProperty("subtype").GetString());

        await fake.PushStdoutAsync("""
        {"type":"control_request","request_id":"req-42","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf /"},"tool_use_id":"toolu_7"}}
        """);

        var permission = (PluginPermissionRequested)await _ReadEventAsync(driver, e => e is PluginPermissionRequested);
        Assert.Equal("toolu_7", permission.ToolUseId);
        Assert.Equal("Bash", permission.ToolName);

        await driver.RespondToPermissionAsync("toolu_7", allow: false, CancellationToken.None);

        // The deny is written back as a control_response keyed on the CLI's own request_id, not the tool_use_id.
        var response = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("response");
        Assert.Equal("req-42", response.GetProperty("request_id").GetString());
        Assert.Equal("deny", response.GetProperty("response").GetProperty("behavior").GetString());
    }

    [Fact]
    public async Task CanUseTool_Allow_EchoesTheOriginalToolInputAsUpdatedInput()
    {
        // Proven red before the fix: the driver hard-coded originalInputJson to "{}", so an approved Bash call went
        // back with updatedInput:{} and the CLI would have run it with no command at all. The real input the CLI sent
        // must ride back verbatim — the cockpit approves the call, it does not rewrite it.
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await fake.PushStdoutAsync("""
        {"type":"control_request","request_id":"req-5","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls -la"},"tool_use_id":"toolu_3"}}
        """);
        await _ReadEventAsync(driver, e => e is PluginPermissionRequested);

        await driver.RespondToPermissionAsync("toolu_3", allow: true, CancellationToken.None);

        var decision = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("response").GetProperty("response");
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
        Assert.Equal("ls -la", decision.GetProperty("updatedInput").GetProperty("command").GetString());
    }

    [Fact]
    public async Task RespondToPermission_ForUnknownTool_WritesNothing()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        var writtenAfterStart = fake.WrittenLines.Count;

        // No pending approval under this id (the CLI auto-allowed, or it already resolved) — nothing to feed back.
        await driver.RespondToPermissionAsync("never-seen", allow: true, CancellationToken.None);

        Assert.Equal(writtenAfterStart, fake.WrittenLines.Count);
    }

    [Fact]
    public async Task StreamJsonLine_IsMappedToTranscriptEvents()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await fake.PushStdoutAsync("""
        {"type":"stream_event","session_id":"s-1","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}}
        """);

        var delta = (PluginAssistantTextDelta)await _ReadEventAsync(driver, e => e is PluginAssistantTextDelta);
        Assert.Equal("Hi", delta.Text);
    }

    [Fact]
    public async Task SendUserMessage_WritesTheStreamJsonUserPayload()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SendUserMessageAsync("hello", CancellationToken.None);

        var payload = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement;
        Assert.Equal("user", payload.GetProperty("type").GetString());
        var message = payload.GetProperty("message");
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("hello", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task SendUserMessage_WithImages_WritesTextAndImageContentBlocks()
    {
        // Regression: moving Claude to a plugin must not lose image input the in-tree route had. With an attachment the
        // content becomes an array (a text block + one base64 image block), not a plain string.
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SendUserMessageAsync(
            "what is this?",
            new[] { new PluginImageAttachment("image/png", "aGVsbG8=") },
            CancellationToken.None);

        var content = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("what is this?", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        var source = content[1].GetProperty("source");
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal("aGVsbG8=", source.GetProperty("data").GetString());
    }

    [Fact]
    public void Capabilities_ReportSupportsVision()
    {
        var fake = new FakeClaudeSdkSubprocess();
        var driver = _CreateDriver(fake);

        Assert.True(driver.Capabilities.SupportsVision);
    }

    [Fact]
    public async Task SetLiveOption_Model_SendsSetModelControlRequest()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: "opus", workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SetLiveOptionAsync(ClaudeSdkSessionDriver.ModelOptionKey, "sonnet", CancellationToken.None);

        var request = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("request");
        Assert.Equal("set_model", request.GetProperty("subtype").GetString());
        Assert.Equal("sonnet", request.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SetLiveOption_Effort_SwitchesTheThinkingTokenBudget_ForTheLevel()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SetLiveOptionAsync(ClaudeSdkSessionDriver.EffortOptionKey, "high", CancellationToken.None);

        // Effort is the CLI's thinking-token budget (set_max_thinking_tokens); "high" maps to the plugin's own
        // per-level tuning (24k) — the same budget the host's SessionOptionCatalog carried before Claude became a plugin.
        // The field is snake_case (max_thinking_tokens) exactly as the Agent SDK's Query.set_max_thinking_tokens
        // writes it; camelCase is silently dropped by the CLI, so the budget would never change — the effort-not-live bug.
        var request = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("request");
        Assert.Equal("set_max_thinking_tokens", request.GetProperty("subtype").GetString());
        Assert.Equal(24_000, request.GetProperty("max_thinking_tokens").GetInt32());
    }

    [Fact]
    public async Task LiveOptions_IncludeEffort_WithFriendlyLabels()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        var effort = driver.LiveOptions.Single(option => option.Key == ClaudeSdkSessionDriver.EffortOptionKey);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, effort.Choices);
        Assert.Equal("Extra high", effort.ChoiceLabels!["xhigh"]);
        Assert.Equal("medium", effort.DefaultValue);
    }

    [Fact]
    public async Task LiveOptions_PermissionMode_ExcludesBypass_WhichIsLaunchOnly()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        var permissionOption = driver.LiveOptions.Single(option => option.Key == ClaudeSdkSessionDriver.PermissionModeOptionKey);
        Assert.Equivalent(new object[] { "default", "acceptEdits", "plan" }, permissionOption.Choices);
        Assert.DoesNotContain("bypassPermissions", permissionOption.Choices);
    }

    [Fact]
    public async Task LiveOptions_OmitPermissionMode_WhenLaunchedInBypass()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(
            model: null,
            workingDirectory: _tempDir,
            resumeSessionId: null,
            options: new Dictionary<string, string> { ["permission-mode"] = "bypassPermissions" },
            mcpServers: null,
            CancellationToken.None);

        // Bypass cannot be left mid-session, so no live permission-mode switch is offered at all.
        Assert.DoesNotContain(driver.LiveOptions, option => option.Key == ClaudeSdkSessionDriver.PermissionModeOptionKey);
    }

    // The profile's environment variables (AC-22) ride the environment-carrying StartAsync overload into the
    // spawn; the driver's own rules — the ANTHROPIC_* drop and the config-dir export — keep the last word.
    [Fact]
    public async Task Start_AppliesTheProfilesEnvironmentVariablesToTheSpawn()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            environment: new Dictionary<string, string> { ["AI_OS_ROOT"] = "/home/raymond/AI-OS" },
            CancellationToken.None);

        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("AI_OS_ROOT", "/home/raymond/AI-OS"), fake.EnvironmentVariables);
    }

    // AC-146: sub-agent activity is worth seeing by default (Raymond, 2026-07-29) — an env var rather than a CLI
    // flag, since an older CLI that does not know it just ignores it, where an unknown flag would refuse to start.
    [Fact]
    public async Task Start_SetsTheForwardSubagentTextEnvironmentVariable_OnByDefault()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            CancellationToken.None);

        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("CLAUDE_CODE_FORWARD_SUBAGENT_TEXT", "1"), fake.EnvironmentVariables);
    }

    [Fact]
    public async Task Start_AProfileSuppliedForwardSubagentTextValue_IsNotOverridden()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            environment: new Dictionary<string, string> { ["CLAUDE_CODE_FORWARD_SUBAGENT_TEXT"] = "0" },
            CancellationToken.None);

        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("CLAUDE_CODE_FORWARD_SUBAGENT_TEXT", "0"), fake.EnvironmentVariables);
    }

    [Fact]
    public async Task Start_AProfileSuppliedAnthropicCredential_IsRemovedFromTheSpawnNotHandedToTheCli()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            environment: new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "smuggled" },
            CancellationToken.None);

        // Null = remove at the subprocess seam: the key must be an explicit removal, never the smuggled value.
        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("ANTHROPIC_API_KEY", null), fake.EnvironmentVariables);
    }

    [Fact]
    public async Task Start_AProfileVariableCannotRedirectTheConfigDir_TheProfilesOwnDirWins()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            environment: new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = "/somebody/elses/profile" },
            CancellationToken.None);

        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("CLAUDE_CONFIG_DIR", _tempDir), fake.EnvironmentVariables);
    }

    // The host's marker for "nobody is watching this one" — what a delegated task and a self-driving Autopilot step
    // carry.
    private static Dictionary<string, string> _Unattended() =>
        new(StringComparer.OrdinalIgnoreCase) { [WellKnownPluginSessionOptions.Unattended] = "true" };

    // What the host states for a pane the operator opened. Explicit, not absent: absence means a host too old to
    // answer, which the driver must read as unattended.
    private static Dictionary<string, string> _Attended() =>
        new(StringComparer.OrdinalIgnoreCase) { [WellKnownPluginSessionOptions.Unattended] = "false" };

    // AC-378: an unattended session with registry servers resolved carries --strict-mcp-config alongside
    // --mcp-config, so the CLI never unions in its own user/project claude.ai-connectors on top of what the
    // resolution produced.
    [Fact]
    public async Task Start_Unattended_WithMcpServers_SpawnsWithStrictMcpConfig()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: _Unattended(),
            mcpServers: [new PluginMcpServer { Name = "youtrack", Url = "http://example/mcp" }],
            CancellationToken.None);

        Assert.Contains("--mcp-config", fake.Arguments!);
        Assert.Contains("--strict-mcp-config", fake.Arguments!);
    }

    // The re-cut of AC-378 onto the attended/unattended axis: a pane the operator opened in SDK mode gets the same
    // union an interactive TTY session gets, so the account's own claude.ai connectors survive. Strict here was the
    // regression ClaudeTtyProviderTests guards the TTY route against, reached through the other route.
    [Fact]
    public async Task Start_Attended_WithMcpServers_SpawnsWithoutStrictMcpConfig()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: _Attended(),
            mcpServers: [new PluginMcpServer { Name = "youtrack", Url = "http://example/mcp" }],
            CancellationToken.None);

        Assert.Contains("--mcp-config", fake.Arguments!);
        Assert.DoesNotContain("--strict-mcp-config", fake.Arguments!);
    }

    // Fail-closed on a host that predates the attended/unattended split and states neither: absence must read as
    // unattended, so a delegated session on such a host keeps the AC-378 guarantee instead of quietly regaining the
    // operator's own account connectors.
    [Fact]
    public async Task Start_WithNoAttendanceStated_FallsBackToStrict()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null,
            mcpServers: [new PluginMcpServer { Name = "youtrack", Url = "http://example/mcp" }],
            CancellationToken.None);

        Assert.Contains("--strict-mcp-config", fake.Arguments!);
    }

    // AC-378, criterion 4 — the empty-resolution trap: an unattended narrowing that resolves to zero eligible
    // servers must still spawn with an explicit (empty) --mcp-config and --strict-mcp-config, never with the flag
    // dropped entirely (which would let the CLI fall back to its own full user/project config — MORE servers than
    // an empty, narrowed resolution asked for).
    [Fact]
    public async Task Start_Unattended_WithNoMcpServers_StillSpawnsWithAnExplicitEmptyMcpConfig_AndStrict()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: _Unattended(),
            mcpServers: [],
            CancellationToken.None);

        Assert.Contains("--mcp-config", fake.Arguments!);
        Assert.Contains("--strict-mcp-config", fake.Arguments!);

        var mcpConfigIndex = fake.Arguments!.ToList().IndexOf("--mcp-config");
        var path = fake.Arguments![mcpConfigIndex + 1];
        Assert.True(File.Exists(path), "the strict path must write an explicit config file rather than dropping the flag");
        Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject());
    }

    // Same as above for the mcpServers: null case (a route that never even attempted resolution) — must behave
    // exactly like an empty list, not fall back to dropping --mcp-config.
    [Fact]
    public async Task Start_Unattended_WithNullMcpServers_StillSpawnsWithAnExplicitEmptyMcpConfig_AndStrict()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: _Unattended(), mcpServers: null, CancellationToken.None);

        Assert.Contains("--mcp-config", fake.Arguments!);
        Assert.Contains("--strict-mcp-config", fake.Arguments!);
    }

    // The attended mirror of the empty-resolution case: with no strict flag to pair with, an empty config file would
    // only strip the operator's own user/project servers for nothing, so --mcp-config stays off — the TTY route's
    // behaviour, which is the point of the re-cut.
    [Fact]
    public async Task Start_Attended_WithNoMcpServers_OmitsMcpConfigEntirely()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: _Attended(), mcpServers: [], CancellationToken.None);

        Assert.DoesNotContain("--mcp-config", fake.Arguments!);
        Assert.DoesNotContain("--strict-mcp-config", fake.Arguments!);
    }

    [Fact]
    public async Task ARealTurnPushedDownTheStdoutPump_ReachesTheDriversStatusFeed()
    {
        // AC-530: the seam the host polls is IPluginSessionDriver.Status, a member whose interface default is null —
        // so the arithmetic being right proves nothing until the driver actually overrides and feeds it. This drives
        // the verbatim CLI 2.1.220 capture through the real stdout pump and reads the property the host reads.
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        Assert.Null(driver.Status);

        foreach (var line in ClaudeSdkUsageTests.RealTurnLines)
        {
            await fake.PushStdoutAsync(line);
        }

        // The result line is what closes the turn, and the host reads Status off the back of that event — so waiting
        // for it is exactly the moment the header would look. Nothing answers the usage poll here, so this waits out
        // the publish grace: the assertion below is about the rate-limit line the stream carried on its own.
        await _ReadEventAsync(driver, e => e is PluginTurnCompleted);

        var status = driver.Status;
        Assert.NotNull(status);
        Assert.True(status.HasAny);
        var window = Assert.Single(status.RateLimits);
        Assert.Equal("wk", window.Label);
        Assert.Equal(98d, window.UsedPercent, precision: 10);
    }

    // The turn boundary is where the driver asks the CLI for the two figures the pill renders, and this drives the
    // whole round-trip through the real pump: the result line goes down stdout, the requests come back up stdin,
    // their replies go down stdout again.
    //
    // What it pins down is the *ordering*. The host reads Status exactly once per turn, off the back of
    // TurnCompleted (SessionViewModel._RefreshLimits — no timer, no second read), so the figures have to be in
    // before that event goes out. Asserting them after the event would pass just as well with the poll landing a
    // turn late, which is the bug this ordering exists to prevent. The subtypes are asserted by name because they
    // are the wire contract with the CLI — a typo would leave the pill silently blank.
    [Fact]
    public async Task AtTheTurnBoundary_BothFiguresAreInBeforeTheTurnEventGoesOut()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await fake.PushStdoutAsync("""{"type":"result","subtype":"success","session_id":"s","is_error":false}""");

        var usageId = await _AwaitControlRequestAsync(fake, "get_usage");
        await fake.PushStdoutAsync(_ControlSuccess(usageId, """
        {"rate_limits":{"five_hour":{"utilization":7,"resets_at":"2026-08-08T18:00:00.978410+00:00"},"seven_day":{"utilization":1,"resets_at":"2026-08-15T09:00:00.978430+00:00"}}}
        """));

        var contextId = await _AwaitControlRequestAsync(fake, "get_context_usage");
        await fake.PushStdoutAsync(_ControlSuccess(contextId, """{"totalTokens":28981,"maxTokens":1000000,"percentage":3}"""));

        // Read at the very moment the header would look, not a poll later.
        await _ReadEventAsync(driver, e => e is PluginTurnCompleted);

        var status = driver.Status;
        Assert.NotNull(status);
        Assert.Equal(3d, status.ContextUsedPercent);
        Assert.Equal(["5h", "wk"], status.RateLimits.Select(window => window.Label));
        Assert.Equal(7d, status.RateLimits[0].UsedPercent, precision: 10);
    }

    // The turn must never be held hostage to a nicety: a CLI that answers neither request still completes the turn,
    // just without fresh figures. Without the grace this would wait out `_UsageRequestTimeout` (15s) and the
    // session would look stuck.
    [Fact]
    public async Task ACliThatNeverAnswersThePoll_StillCompletesTheTurn()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await fake.PushStdoutAsync("""{"type":"result","subtype":"success","session_id":"s","is_error":false}""");

        var completed = await _ReadEventAsync(driver, e => e is PluginTurnCompleted);

        Assert.IsType<PluginTurnCompleted>(completed);
        Assert.Null(driver.Status);
    }

    // A refused request must release its awaiter rather than let the poll wait out its timeout — otherwise the
    // context figure behind it never gets asked for at all.
    [Fact]
    public async Task AControlRequestTheCliRefuses_DoesNotStallTheRestOfThePoll()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await fake.PushStdoutAsync("""{"type":"result","subtype":"success","session_id":"s","is_error":false}""");

        var usageId = await _AwaitControlRequestAsync(fake, "get_usage");
        await fake.PushStdoutAsync(_ControlError(usageId));

        var contextId = await _AwaitControlRequestAsync(fake, "get_context_usage");
        await fake.PushStdoutAsync(_ControlSuccess(contextId, """{"percentage":42}"""));

        var status = await _AwaitAsync(() => driver.Status?.ContextUsedPercent is not null ? driver.Status : null);
        Assert.Equal(42d, status.ContextUsedPercent);
        Assert.Empty(status.RateLimits);
    }

    // The CLI's reply envelope, verbatim from a live 2.1.226 session.
    private static string _ControlSuccess(string requestId, string payloadJson) =>
        $$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{requestId}}}","response":{{{payloadJson.Trim()}}}}}""";

    private static string _ControlError(string requestId) =>
        $$$"""{"type":"control_response","response":{"subtype":"error","request_id":"{{{requestId}}}","error":"not supported in this context"}}""";

    // The request_id of the newest control_request carrying `subtype`, once the fire-and-forget poll has written it.
    private static async Task<string> _AwaitControlRequestAsync(FakeClaudeSdkSubprocess fake, string subtype) =>
        await _AwaitAsync(() =>
        {
            foreach (var line in fake.WrittenLines.Reverse())
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("request", out var request)
                    && request.TryGetProperty("subtype", out var written)
                    && written.GetString() == subtype)
                {
                    return root.GetProperty("request_id").GetString();
                }
            }

            return null;
        });

    // Spins until the poll's own task has run. Polling rather than awaiting because the driver deliberately does not
    // expose the fire-and-forget task — the pump must never block on it.
    private static async Task<T> _AwaitAsync<T>(Func<T?> read) where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (read() is { } value)
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The driver never produced what the test was waiting for.");
    }

    private ClaudeSdkSessionDriver _CreateDriver(FakeClaudeSdkSubprocess fake) =>
        // A temp config dir keeps StartAsync's workspace-trust write off the real ~/.claude.json.
        new(() => fake, new ClaudeProviderConfig(ConfigDir: _tempDir), executablePath: "claude");

    private static async Task<PluginSessionEvent> _ReadEventAsync(ClaudeSdkSessionDriver driver, Func<PluginSessionEvent, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            if (predicate(evt))
            {
                return evt;
            }
        }

        throw new InvalidOperationException("The expected event never arrived before the stream completed.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
