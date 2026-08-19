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

        // StartAsync puts an SDK client on the control channel first (so the CLI routes approvals here), applies
        // the launch effort as the session's initial thinking-token budget (default medium), then polls usage.
        Assert.Equal(
            ["initialize", "set_max_thinking_tokens", "get_usage", "get_context_usage"],
            fake.WrittenLines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("request").GetProperty("subtype").GetString()));

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
    public async Task CompactContext_WritesTheSlashCommandAsAUserMessage_NotAControlRequest()
    {
        // AC-664: the CLI has no compaction subtype on the control protocol — it parses `/compact` out of the
        // stream-json user input itself, which is why this rides the ordinary user-message line rather than the
        // control channel every other live switch uses. Measured against a live 2.1.226 spawn in this same mode.
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.CompactContextAsync(CancellationToken.None);

        var payload = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement;
        Assert.Equal("user", payload.GetProperty("type").GetString());
        Assert.Equal("/compact", payload.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void Capabilities_VouchForCompactingTheConversation_SoTheHostAsksInsteadOfStartingAFreshOne()
    {
        // The capability is the whole gate: an assistant whose context fills up asks a provider that reports this
        // to summarise, and throws the conversation away only on one that does not.
        var fake = new FakeClaudeSdkSubprocess();
        var driver = _CreateDriver(fake);

        Assert.True(driver.Capabilities.SupportsContextCompaction);
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

        // The fake refuses both polls, so nothing here feeds Status; the assertion below is about the rate-limit
        // line the stream carried on its own.
        await _ReadEventAsync(driver, e => e is PluginTurnCompleted);

        var status = driver.Status;
        Assert.NotNull(status);
        Assert.True(status.HasAny);
        var window = Assert.Single(status.RateLimits);
        Assert.Equal("wk", window.Label);
        Assert.Equal(98d, window.UsedPercent, precision: 10);
    }

    // The whole round-trip through the real pump, and specifically the *ordering*: the host reads Status once per
    // turn, off the back of TurnCompleted, so the figures must be in before it goes out. Asserting after that
    // event would pass just as well with the poll landing a turn late. The subtypes are named because they are
    // the wire contract — a typo leaves the pill silently blank.
    [Fact]
    public async Task AtTheTurnBoundary_BothFiguresAreInBeforeTheTurnEventGoesOut()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        // The start poll is the fake's to refuse; this turn's is the test's to answer.
        fake.AutoRefuseUsagePolls = false;
        var writtenAfterStart = fake.WrittenLines.Count;

        await fake.PushStdoutAsync("""{"type":"result","subtype":"success","session_id":"s","is_error":false}""");

        var usageId = await _AwaitControlRequestAsync(fake, "get_usage", writtenAfterStart);
        await fake.PushStdoutAsync(_ControlSuccess(usageId, """
        {"rate_limits":{"five_hour":{"utilization":7,"resets_at":"2026-08-08T18:00:00.978410+00:00"},"seven_day":{"utilization":1,"resets_at":"2026-08-15T09:00:00.978430+00:00"}}}
        """));

        var contextId = await _AwaitControlRequestAsync(fake, "get_context_usage", writtenAfterStart);
        await fake.PushStdoutAsync(_ControlSuccess(contextId, """{"totalTokens":28981,"maxTokens":1000000,"percentage":3}"""));

        // Read at the very moment the header would look, not a poll later.
        await _ReadEventAsync(driver, e => e is PluginTurnCompleted);

        var status = driver.Status;
        Assert.NotNull(status);
        Assert.Equal(3d, status.ContextUsedPercent);
        Assert.Equal(["5h", "wk"], status.RateLimits.Select(window => window.Label));
        Assert.Equal(7d, status.RateLimits[0].UsedPercent, precision: 10);
    }

    // AC-660: a resumed conversation already has real figures the CLI can report before any turn runs — measured
    // against the reported bug (3 of 4 open panes showing no pill at all, the one difference being that the
    // operator had actually prompted the fourth). Proven red before the fix: StartAsync only ever wrote the
    // initialize/set_max_thinking_tokens lines, so Status stayed null for a resumed-but-idle pane until its first
    // turn completed in this process.
    [Fact]
    public async Task Resuming_PollsUsageDuringStart_SoStatusIsKnownBeforeAnyTurn()
    {
        var fake = new FakeClaudeSdkSubprocess { AutoRefuseUsagePolls = false };
        await using var driver = _CreateDriver(fake);

        var startTask = driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: "conv-1", options: null, mcpServers: null, CancellationToken.None);

        var usageId = await _AwaitControlRequestAsync(fake, "get_usage");
        await fake.PushStdoutAsync(_ControlSuccess(usageId, """
        {"rate_limits":{"five_hour":{"utilization":12,"resets_at":"2026-08-08T18:00:00.978410+00:00"}}}
        """));

        var contextId = await _AwaitControlRequestAsync(fake, "get_context_usage");
        await fake.PushStdoutAsync(_ControlSuccess(contextId, """{"totalTokens":1000,"maxTokens":100000,"percentage":37}"""));

        await startTask;

        var status = driver.Status;
        Assert.NotNull(status);
        Assert.Equal(37d, status.ContextUsedPercent);
        var window = Assert.Single(status.RateLimits);
        Assert.Equal("5h", window.Label);
        Assert.Equal(12d, window.UsedPercent, precision: 10);
    }

    // AC-701: the allowances are account-wide and the context is non-zero from the system prompt alone, so a fresh
    // session has real figures to report before its first turn — AC-660 scoped this poll to resume on the opposite
    // assumption, which left every fresh pane without a usage pill until a turn completed.
    [Fact]
    public async Task AFreshStart_PollsUsageToo_SoStatusIsKnownBeforeAnyTurn()
    {
        var fake = new FakeClaudeSdkSubprocess { AutoRefuseUsagePolls = false };
        await using var driver = _CreateDriver(fake);

        var startTask = driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        var usageId = await _AwaitControlRequestAsync(fake, "get_usage");
        await fake.PushStdoutAsync(_ControlSuccess(usageId, """
        {"rate_limits":{"five_hour":{"utilization":15,"resets_at":"2026-08-11T18:00:00.978410+00:00"}}}
        """));

        var contextId = await _AwaitControlRequestAsync(fake, "get_context_usage");
        await fake.PushStdoutAsync(_ControlSuccess(contextId, """{"totalTokens":2000,"maxTokens":100000,"percentage":2}"""));

        await startTask;

        var status = driver.Status;
        Assert.NotNull(status);
        Assert.Equal(2d, status.ContextUsedPercent);
        var window = Assert.Single(status.RateLimits);
        Assert.Equal("5h", window.Label);
        Assert.Equal(15d, window.UsedPercent, precision: 10);
    }

    // AC-761 F2 / acceptance criterion 5: a cold get_context_usage (~1.5s) alongside a get_usage (~0.7s) still
    // lands inside the widened grace, because the two now run in parallel — sequentially they would sum to
    // ~2.2s, past the old 2s grace, and the second request would not even have been sent yet at that point.
    [Fact]
    public async Task ACold0_7sUsageReplyAndA1_5sContextReply_BothLandWithinTheGrace()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        fake.AutoRefuseUsagePolls = false;
        var writtenAfterStart = fake.WrittenLines.Count;

        await fake.PushStdoutAsync("""{"type":"result","subtype":"success","session_id":"s","is_error":false}""");

        var usageId = await _AwaitControlRequestAsync(fake, "get_usage", writtenAfterStart);
        var contextId = await _AwaitControlRequestAsync(fake, "get_context_usage", writtenAfterStart);

        _ = Task.Delay(TimeSpan.FromMilliseconds(700))
            .ContinueWith(_ => fake.PushStdoutAsync(_ControlSuccess(usageId, """{"rate_limits":{"five_hour":{"utilization":9}}}""")));
        _ = Task.Delay(TimeSpan.FromMilliseconds(1500))
            .ContinueWith(_ => fake.PushStdoutAsync(_ControlSuccess(contextId, """{"percentage":3}""")));

        await _ReadEventAsync(driver, e => e is PluginTurnCompleted);

        var status = driver.Status;
        Assert.NotNull(status);
        Assert.Equal(3d, status.ContextUsedPercent);
        var window = Assert.Single(status.RateLimits);
        Assert.Equal(9d, window.UsedPercent, precision: 10);
    }

    // Without the grace this waits out `_UsageRequestTimeout` (15s) and the session looks stuck.
    [Fact]
    public async Task ACliThatNeverAnswersThePoll_StillCompletesTheTurn()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        fake.AutoRefuseUsagePolls = false;

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
        fake.AutoRefuseUsagePolls = false;
        var writtenAfterStart = fake.WrittenLines.Count;

        await fake.PushStdoutAsync("""{"type":"result","subtype":"success","session_id":"s","is_error":false}""");

        var usageId = await _AwaitControlRequestAsync(fake, "get_usage", writtenAfterStart);
        await fake.PushStdoutAsync(_ControlError(usageId));

        var contextId = await _AwaitControlRequestAsync(fake, "get_context_usage", writtenAfterStart);
        await fake.PushStdoutAsync(_ControlSuccess(contextId, """{"percentage":42}"""));

        var status = await _AwaitAsync(() => driver.Status?.ContextUsedPercent is not null ? driver.Status : null);
        Assert.Equal(42d, status.ContextUsedPercent);
        Assert.Empty(status.RateLimits);
    }

    // AC-539: a resume the CLI cannot resolve prints its result line and exits immediately (measured against
    // claude.exe), so stdout ends while that line's publish is still parked behind the usage poll. Completing the
    // stream there dropped the turn — and the host's restore banner only degrades to "Gone" on a TurnCompleted.
    [Fact]
    public async Task AResultLineFollowedByTheProcessExiting_StillReachesTheHost()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: "gone", options: null, mcpServers: null, CancellationToken.None);
        fake.AutoRefuseUsagePolls = false;

        await fake.PushStdoutAsync("""
        {"type":"result","subtype":"error_during_execution","session_id":"gone","is_error":true,"errors":["No conversation found with session ID: gone"]}
        """);

        // No control reply is ever pushed — a dead CLI answers nothing, so the usage poll waits out its grace.
        fake.CompleteStdout();

        var completed = (PluginTurnCompleted)await _ReadEventAsync(driver, e => e is PluginTurnCompleted);
        Assert.True(completed.IsError);
        Assert.Equal("error_during_execution", completed.Subtype);
        Assert.NotNull(completed.Errors);
        Assert.Equal("No conversation found with session ID: gone", Assert.Single(completed.Errors));
    }

    // AC-943: interrupting a turn parked on a permission prompt must not leave it dangling — the driver denies it
    // on the wire itself, in case the CLI never sends the `control_cancel_request` that would otherwise retract it.
    [Fact]
    public async Task InterruptAsync_WithAPendingApproval_SendsInterruptThenDeniesIt()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await fake.PushStdoutAsync("""
        {"type":"control_request","request_id":"req-9","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"},"tool_use_id":"toolu_9"}}
        """);
        await _ReadEventAsync(driver, e => e is PluginPermissionRequested);
        var writtenBeforeInterrupt = fake.WrittenLines.Count;

        await driver.InterruptAsync();

        var interruptLine = JsonDocument.Parse(fake.WrittenLines[writtenBeforeInterrupt]).RootElement;
        Assert.Equal("interrupt", interruptLine.GetProperty("request").GetProperty("subtype").GetString());

        var denyResponse = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("response");
        Assert.Equal("req-9", denyResponse.GetProperty("request_id").GetString());
        Assert.Equal("deny", denyResponse.GetProperty("response").GetProperty("behavior").GetString());

        // Already answered on the wire — a later click must write nothing more.
        var writtenAfterInterrupt = fake.WrittenLines.Count;
        await driver.RespondToPermissionAsync("toolu_9", allow: true, CancellationToken.None);
        Assert.Equal(writtenAfterInterrupt, fake.WrittenLines.Count);
    }

    [Fact]
    public async Task InterruptAsync_WithNoPendingApproval_OnlySendsTheInterruptLine()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        var writtenBeforeInterrupt = fake.WrittenLines.Count;

        await driver.InterruptAsync();

        Assert.Equal(writtenBeforeInterrupt + 1, fake.WrittenLines.Count);
        var interruptLine = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement;
        Assert.Equal("interrupt", interruptLine.GetProperty("request").GetProperty("subtype").GetString());
    }

    // The CLI's own retraction signal (see `ClaudeControlProtocol`) — dropped silently before this fix, leaving the
    // entry stale for the rest of the session.
    [Fact]
    public async Task ControlCancelRequest_RemovesThePendingApproval_SoALaterRespondWritesNothing()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await fake.PushStdoutAsync("""
        {"type":"control_request","request_id":"req-13","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"},"tool_use_id":"toolu_13"}}
        """);
        await _ReadEventAsync(driver, e => e is PluginPermissionRequested);

        await fake.PushStdoutAsync("""{"type":"control_cancel_request","request_id":"req-13"}""");

        // Both lines run on the same sequential stdout pump — waiting for this unrelated event to surface proves
        // the cancel ahead of it was already handled.
        await fake.PushStdoutAsync("""
        {"type":"stream_event","session_id":"s-1","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"."}}}
        """);
        await _ReadEventAsync(driver, e => e is PluginAssistantTextDelta);

        var writtenBeforeRespond = fake.WrittenLines.Count;
        await driver.RespondToPermissionAsync("toolu_13", allow: true, CancellationToken.None);
        Assert.Equal(writtenBeforeRespond, fake.WrittenLines.Count);
    }

    // The CLI's reply envelope, verbatim from a live 2.1.226 session.
    private static string _ControlSuccess(string requestId, string payloadJson) =>
        $$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{requestId}}}","response":{{{payloadJson.Trim()}}}}}""";

    private static string _ControlError(string requestId) =>
        $$$"""{"type":"control_response","response":{"subtype":"error","request_id":"{{{requestId}}}","error":"not supported in this context"}}""";

    // The request_id of the newest control_request carrying `subtype`, once the fire-and-forget poll has written it.
    // `after` skips the lines a start already wrote, so a turn's poll is not answered on the start poll's id.
    private static async Task<string> _AwaitControlRequestAsync(FakeClaudeSdkSubprocess fake, string subtype, int after = 0) =>
        await _AwaitAsync(() =>
        {
            foreach (var line in fake.WrittenLines.Skip(after).Reverse())
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

    // Polled rather than awaited: the driver does not expose the fire-and-forget task, by design.
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
